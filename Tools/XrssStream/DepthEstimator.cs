using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

/// <summary>
/// Depth Lab (live): an optional monocular depth model on the PC's GPU (DirectML). Used only when a headset asks for depth on a stream ('D' request); with no
/// request nothing here is loaded or run. The result is a small grayscale picture (255 = near) that the headset uses to displace the panel.
/// Model: Depth Anything V2 small (ONNX). Looked up in XRSS_DEPTH_MODEL, then %LOCALAPPDATA%\XrSpatialScreens\models\depth_anything_v2_small.onnx, then next to the exe.
/// </summary>
static class DepthEstimator
{
    const int Size = 518;                                           // the model's input edge (a multiple of 14)
    static InferenceSession s_Session;
    static string s_InputName;
    static bool s_Tried;
    static readonly object s_Lock = new object();
    static readonly float[] s_Input = new float[3 * Size * Size];

    public static string Problem { get; private set; }
    public static bool Ready { get { EnsureLoaded(); return s_Session != null; } }

    static string FindModel()
    {
        string env = Environment.GetEnvironmentVariable("XRSS_DEPTH_MODEL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string a = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "models", "depth_anything_v2_small.onnx");
        if (File.Exists(a)) return a;
        string b = Path.Combine(AppContext.BaseDirectory, "depth_anything_v2_small.onnx");
        return File.Exists(b) ? b : null;
    }

    static void EnsureLoaded()
    {
        lock (s_Lock)
        {
            if (s_Tried) return;
            s_Tried = true;
            string path = FindModel();
            if (path == null) { Problem = "no depth model file (depth_anything_v2_small.onnx)"; Console.WriteLine("depth: " + Problem); return; }
            try
            {
                var so = new SessionOptions();
                // device 0 is the main GPU on this PC (the integrated one is slower by 20x); fall back to the CPU when DirectML cannot start
                try { so.AppendExecutionProvider_DML(0); } catch (Exception e) { Console.WriteLine("depth: DirectML unavailable (" + e.Message + "), using the CPU"); }
                s_Session = new InferenceSession(path, so);
                foreach (var k in s_Session.InputMetadata.Keys) { s_InputName = k; break; }
                Console.WriteLine("depth: model loaded from " + path);
            }
            catch (Exception e) { Problem = "depth model failed to load: " + e.Message; Console.WriteLine("depth: " + Problem); s_Session = null; }
        }
    }

    /// <summary>Per-stream smoothing state: the previous depth picture and a slowly moving value range, so the result does not flicker from frame to frame.</summary>
    public sealed class Smoother { public float[] Prev; public int W, H; public float Min, Max; public bool Init; }

    /// <summary>
    /// Depth for one BGRA frame as an 8-bit picture (255 = near) of at most 256 px wide, same aspect as the frame. Returns null on failure.
    /// </summary>
    public static unsafe byte[] Estimate(byte[] bgra, int w, int h, int stride, Smoother st, out int ow, out int oh)
    {
        ow = oh = 0;
        if (!Ready) return null;
        ow = Math.Min(256, w); oh = Math.Max(16, (int)((long)h * ow / w)); if (oh > 256) { oh = 256; ow = Math.Max(16, (int)((long)w * oh / h)); }
        float[] raw = null;
        lock (s_Lock)
        {
            using (var small = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Opaque)))
            {
                bool ok;
                fixed (byte* sp = bgra)
                using (var src = new SKPixmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), (IntPtr)sp, stride))
                    ok = src.ScalePixels(small.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (!ok) return null;
                byte* p = (byte*)small.GetPixels();
                int plane = Size * Size;
                // ImageNet mean and std, RGB planes
                const float mr = 0.485f, mg = 0.456f, mb = 0.406f, sr = 1f / 0.229f, sg = 1f / 0.224f, sb = 1f / 0.225f;
                for (int i = 0; i < plane; i++)
                {
                    s_Input[i] = (p[i * 4 + 2] / 255f - mr) * sr; s_Input[plane + i] = (p[i * 4 + 1] / 255f - mg) * sg; s_Input[2 * plane + i] = (p[i * 4] / 255f - mb) * sb;
                }
            }
            var tensor = new DenseTensor<float>(s_Input.AsMemory(), new[] { 1, 3, Size, Size });
            using var results = s_Session.Run(new[] { NamedOnnxValue.CreateFromTensor(s_InputName, tensor) });
            foreach (var r in results) { raw = System.Linq.Enumerable.ToArray(r.AsEnumerable<float>()); break; }
        }
        // resample to the output size (the model squashed the frame into a square, so a straight mapping undoes that), bilinear
        var cur = new float[ow * oh];
        float mn = float.MaxValue, mx = float.MinValue;
        for (int y = 0; y < oh; y++)
        {
            float fy = (y + 0.5f) / oh * Size - 0.5f; int y0 = Math.Clamp((int)MathF.Floor(fy), 0, Size - 1), y1 = Math.Min(y0 + 1, Size - 1); float ty = Math.Clamp(fy - y0, 0f, 1f);
            for (int x = 0; x < ow; x++)
            {
                float fx = (x + 0.5f) / ow * Size - 0.5f; int x0 = Math.Clamp((int)MathF.Floor(fx), 0, Size - 1), x1 = Math.Min(x0 + 1, Size - 1); float tx = Math.Clamp(fx - x0, 0f, 1f);
                float v = (raw[y0 * Size + x0] * (1 - tx) + raw[y0 * Size + x1] * tx) * (1 - ty) + (raw[y1 * Size + x0] * (1 - tx) + raw[y1 * Size + x1] * tx) * ty;
                cur[y * ow + x] = v; if (v < mn) mn = v; if (v > mx) mx = v;
            }
        }
        // a slowly moving range (so one odd frame does not rescale the whole scene) and a blend with the previous picture (so the surface does not shimmer)
        if (!st.Init || st.W != ow || st.H != oh) { st.Init = true; st.W = ow; st.H = oh; st.Min = mn; st.Max = mx; st.Prev = null; }
        else { st.Min = st.Min * 0.8f + mn * 0.2f; st.Max = st.Max * 0.8f + mx * 0.2f; }
        float range = Math.Max(1e-4f, st.Max - st.Min);
        var outb = new byte[ow * oh];
        bool blend = st.Prev != null;
        if (!blend) st.Prev = new float[ow * oh];
        for (int i = 0; i < cur.Length; i++)
        {
            float d = Math.Clamp((cur[i] - st.Min) / range, 0f, 1f);
            if (blend) d = st.Prev[i] * 0.5f + d * 0.5f;
            st.Prev[i] = d;
            outb[i] = (byte)Math.Round(d * 255f);
        }
        return outb;
    }
}

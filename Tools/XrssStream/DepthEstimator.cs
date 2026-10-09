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
    static bool s_Tried, s_V3;                                       // s_V3: Depth Anything 3 small (switched on by the file modelsSe_v3.flag), else Depth Anything V2 small
    static readonly object s_Lock = new object();
    static float[] s_Input = new float[3 * Size * Size];

    public static string Problem { get; private set; }
    public static bool Ready { get { EnsureLoaded(); return s_Session != null; } }

    static string FindModel()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "models");
        string v3 = Path.Combine(baseDir, "da3", "model.onnx");
        s_V3 = File.Exists(Path.Combine(baseDir, "use_v3.flag")) && File.Exists(v3);
        if (s_V3) return v3;
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
                Console.WriteLine("depth: model loaded from " + path + (s_V3 ? " (Depth Anything 3)" : " (Depth Anything V2)"));
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
        int gw, gh;                                                  // the grid the model works on
        if (s_V3) { int le = 504; gw = w >= h ? le / 14 * 14 : Math.Max(14, (int)Math.Round((double)w * le / h / 14) * 14); gh = w >= h ? Math.Max(14, (int)Math.Round((double)h * le / w / 14) * 14) : le / 14 * 14; }
        else { gw = gh = Size; }                                     // V2 takes a square (the frame is squashed into it; the straight resample below undoes that)
        lock (s_Lock)
        {
            int plane = gw * gh;
            var input = s_Input.Length >= 3 * plane ? s_Input : new float[3 * plane];
            using (var small = new SKBitmap(new SKImageInfo(gw, gh, SKColorType.Bgra8888, SKAlphaType.Opaque)))
            {
                bool ok;
                fixed (byte* sp = bgra)
                using (var src = new SKPixmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), (IntPtr)sp, stride))
                    ok = src.ScalePixels(small.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (!ok) return null;
                byte* p = (byte*)small.GetPixels();
                // ImageNet mean and std, RGB planes
                const float mr = 0.485f, mg = 0.456f, mb = 0.406f, sr = 1f / 0.229f, sg = 1f / 0.224f, sb = 1f / 0.225f;
                for (int i = 0; i < plane; i++)
                {
                    input[i] = (p[i * 4 + 2] / 255f - mr) * sr; input[plane + i] = (p[i * 4 + 1] / 255f - mg) * sg; input[2 * plane + i] = (p[i * 4] / 255f - mb) * sb;
                }
            }
            var tensor = new DenseTensor<float>(input.AsMemory(0, 3 * plane), s_V3 ? new[] { 1, 1, 3, gh, gw } : new[] { 1, 3, gh, gw });
            using var results = s_Session.Run(new[] { NamedOnnxValue.CreateFromTensor(s_InputName, tensor) });
            foreach (var r in results) { if (s_V3 && r.Name != "predicted_depth") continue; raw = System.Linq.Enumerable.ToArray(r.AsEnumerable<float>()); break; }
            if (raw == null || raw.Length < plane) return null;
            if (s_V3) for (int i = 0; i < plane; i++) raw[i] = 1f / Math.Max(raw[i], 1e-3f);          // V3 gives depth (far is large); the panel wants near = large
        }
        // resample to the output size, bilinear
        var cur = new float[ow * oh];
        float mn = float.MaxValue, mx = float.MinValue;
        for (int y = 0; y < oh; y++)
        {
            float fy = (y + 0.5f) / oh * gh - 0.5f; int y0 = Math.Clamp((int)MathF.Floor(fy), 0, gh - 1), y1 = Math.Min(y0 + 1, gh - 1); float ty = Math.Clamp(fy - y0, 0f, 1f);
            for (int x = 0; x < ow; x++)
            {
                float fx = (x + 0.5f) / ow * gw - 0.5f; int x0 = Math.Clamp((int)MathF.Floor(fx), 0, gw - 1), x1 = Math.Min(x0 + 1, gw - 1); float tx = Math.Clamp(fx - x0, 0f, 1f);
                float v = (raw[y0 * gw + x0] * (1 - tx) + raw[y0 * gw + x1] * tx) * (1 - ty) + (raw[y1 * gw + x0] * (1 - tx) + raw[y1 * gw + x1] * tx) * ty;
                cur[y * ow + x] = v; if (v < mn) mn = v; if (v > mx) mx = v;
            }
        }
        // a 3x3 blur: one noisy depth pixel must not become a spike on the mesh
        var blur = new float[cur.Length];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float sum = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= ow || yy >= oh) continue;
                        sum += cur[yy * ow + xx]; n++;
                    }
                blur[y * ow + x] = sum / n;
            }
        cur = blur;
        // the value range is the 2nd to 98th percentile (a few stray pixels must not rescale the whole scene) and it moves slowly, so the scene does not breathe
        Range(cur, out float lo, out float hi);
        if (!st.Init || st.W != ow || st.H != oh) { st.Init = true; st.W = ow; st.H = oh; st.Min = lo; st.Max = hi; st.Prev = null; }
        else { st.Min = st.Min * 0.92f + lo * 0.08f; st.Max = st.Max * 0.92f + hi * 0.08f; }
        float range = Math.Max(1e-4f, st.Max - st.Min);
        var outb = new byte[ow * oh];
        bool blend = st.Prev != null;
        if (!blend) st.Prev = new float[ow * oh];
        for (int i = 0; i < cur.Length; i++)
        {
            float d = Math.Clamp((cur[i] - st.Min) / range, 0f, 1f);
            if (blend)
            {
                // a deadband: a surface only moves when its depth really changed (small differences are model noise), and then it follows quickly
                float diff = d - st.Prev[i];
                d = Math.Abs(diff) < 0.04f ? st.Prev[i] : st.Prev[i] + diff * 0.6f;
            }
            st.Prev[i] = d;
            outb[i] = (byte)Math.Round(d * 255f);
        }
        return outb;
    }

    static void Range(float[] v, out float lo, out float hi)
    {
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (float f in v) { if (f < mn) mn = f; if (f > mx) mx = f; }
        var hist = new int[256]; float scale = 255f / Math.Max(1e-6f, mx - mn);
        foreach (float f in v) hist[Math.Clamp((int)((f - mn) * scale), 0, 255)]++;
        int a = (int)(v.Length * 0.02f), b = (int)(v.Length * 0.98f), acc = 0; lo = mn; hi = mx;
        bool gotLo = false;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (!gotLo && acc >= a) { lo = mn + i / scale; gotLo = true; }
            if (acc >= b) { hi = mn + (i + 1) / scale; break; }
        }
    }
}

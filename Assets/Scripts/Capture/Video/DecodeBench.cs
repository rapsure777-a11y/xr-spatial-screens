#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Benchmark only (no effect on the app): if <persistentDataPath>/bench contains Annex-B clips (*.h264 / *.hevc, access units delimited by AUD NAL units),
    /// decodes each with the headset's MediaCodec decoder as fast as it will go and logs the throughput. Lines start "[XrSpatial] bench".
    /// Answers whether software video decode can sustain a 2880-wide stream on the Frame, which has no hardware decoder exposed.
    /// </summary>
    static class DecodeBench
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            string dir = Path.Combine(Application.persistentDataPath, "bench");
            if (!File.Exists(Path.Combine(dir, "run.flag"))) return;                     // opt-in: a stray clip folder must never start the benchmark
            int w = 2880, h = 1200;
            var t = new Thread(() =>
            {
                AndroidJNI.AttachCurrentThread();
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        string mime = f.EndsWith(".h264") ? "video/avc" : f.EndsWith(".hevc") ? "video/hevc" : null;
                        if (mime == null) continue;
                        try { Run(f, mime, w, h); } catch (Exception e) { Debug.Log("[XrSpatial] bench " + Path.GetFileName(f) + " failed: " + e); }
                    }
                }
                finally { AndroidJNI.DetachCurrentThread(); }
            }) { IsBackground = true, Name = "DecodeBench" };
            t.Start();
        }

        static List<(int off, int len)> Split(byte[] d, bool hevc)
        {
            var starts = new List<int>();
            for (int i = 0; i + 4 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                {
                    int type = hevc ? (d[i + 3] >> 1) & 0x3f : d[i + 3] & 0x1f;
                    if (type == (hevc ? 35 : 9)) starts.Add(i > 0 && d[i - 1] == 0 ? i - 1 : i);
                    i += 2;
                }
            }
            var r = new List<(int, int)>();
            for (int i = 0; i < starts.Count; i++) r.Add((starts[i], (i + 1 < starts.Count ? starts[i + 1] : d.Length) - starts[i]));
            return r;
        }

        static double CpuSeconds() { var f = File.ReadAllText("/proc/self/stat"); var p = f.Substring(f.LastIndexOf(')') + 2).Split(' '); return (long.Parse(p[11]) + long.Parse(p[12])) / 100.0; }

        static unsafe void Run(string file, string mime, int w, int h)
        {
            var data = File.ReadAllBytes(file);
            var aus = Split(data, mime == "video/hevc");
            Debug.Log($"[XrSpatial] bench {Path.GetFileName(file)}: {aus.Count} frames, {data.Length / 1e6:0.0} MB, {data.Length * 8.0 / 1e6 / (aus.Count / 60.0):0} Mbit/s at 60 fps");
            foreach (bool paced in new[] { false, true })
            using (var fmtClass = new AndroidJavaClass("android.media.MediaFormat"))
            using (var codecClass = new AndroidJavaClass("android.media.MediaCodec"))
            using (var fmt = fmtClass.CallStatic<AndroidJavaObject>("createVideoFormat", mime, w, h))
            using (var codec = codecClass.CallStatic<AndroidJavaObject>("createDecoderByType", mime))
            using (var info = new AndroidJavaObject("android.media.MediaCodec$BufferInfo"))
            {
                codec.Call("configure", fmt, null, null, 0);
                codec.Call("start");
                int fed = 0, got = 0, maxLag = 0;
                
                double cpu0 = CpuSeconds();
                var sw = Stopwatch.StartNew();
                var queuedAt = new double[aus.Count];
                var lat = new List<double>();
                while (got < aus.Count && sw.Elapsed.TotalSeconds < 40)
                {
                    double now = sw.Elapsed.TotalMilliseconds;
                    if (fed < aus.Count && (!paced || now >= fed * (1000.0 / 60)))
                    {
                        int ii = codec.Call<int>("dequeueInputBuffer", 0L);
                        if (ii >= 0)
                        {
                            using (var bb = codec.Call<AndroidJavaObject>("getInputBuffer", ii))
                            {
                                IntPtr addr = (IntPtr)AndroidJNI.GetDirectBufferAddress(bb.GetRawObject());
                                var (off, len) = aus[fed];
                                Marshal.Copy(data, off, addr, len);
                                queuedAt[fed] = sw.Elapsed.TotalMilliseconds;
                                codec.Call("queueInputBuffer", ii, 0, len, (long)fed * 1000L, 0);
                            }
                            fed++;
                        }
                    }
                    int oi = codec.Call<int>("dequeueOutputBuffer", info, 1000L);
                    if (oi >= 0)
                    {
                        long idx = info.Get<long>("presentationTimeUs") / 1000L;
                        codec.Call("releaseOutputBuffer", oi, false);
                        got++; maxLag = Math.Max(maxLag, fed - got);
                        if (idx >= 0 && idx < aus.Count) lat.Add(sw.Elapsed.TotalMilliseconds - queuedAt[idx]);
                    }
                }
                double secs = sw.Elapsed.TotalSeconds, cpu = CpuSeconds() - cpu0;
                lat.Sort();
                double med = lat.Count > 0 ? lat[lat.Count / 2] : 0, p95 = lat.Count > 0 ? lat[(int)(lat.Count * 0.95)] : 0, max = lat.Count > 0 ? lat[lat.Count - 1] : 0;
                Debug.Log($"[XrSpatial] bench RESULT {Path.GetFileName(file)} {(paced ? "PACED-60fps" : "UNPACED")} ({codec.Call<string>("getName")}): {got}/{aus.Count} frames in {secs:0.00}s = {got / secs:0.0} fps; frames inside decoder max {maxLag}; input-to-output delay median {med:0.0} ms, p95 {p95:0.0} ms, max {max:0.0} ms; CPU {cpu / secs:0.00} cores");
                codec.Call("stop"); codec.Call("release");
            }
        }
    }
}
#endif

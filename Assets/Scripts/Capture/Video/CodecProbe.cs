#if UNITY_ANDROID && !UNITY_EDITOR
using System.Text;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Log-only: at startup lists the video codecs the headset's Android layer exposes (name, encoder/decoder, hardware or software, size limits, instance limits),
    /// so the hardware-video decision rests on what is really there. Output: logcat, lines starting "[XrSpatial] codec".
    /// </summary>
    static class CodecProbe
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Run()
        {
            try
            {
                using (var list = new AndroidJavaObject("android.media.MediaCodecList", 1))      // 1 = ALL_CODECS
                {
                    var infos = list.Call<AndroidJavaObject[]>("getCodecInfos");
                    int api; using (var v = new AndroidJavaClass("android.os.Build$VERSION")) api = v.GetStatic<int>("SDK_INT");
                    Debug.Log("[XrSpatial] codec probe: " + infos.Length + " codecs, API " + api);
                    foreach (var info in infos)
                    {
                        string name = info.Call<string>("getName");
                        bool enc = info.Call<bool>("isEncoder");
                        string[] types = info.Call<string[]>("getSupportedTypes");
                        foreach (var t in types)
                        {
                            if (!t.StartsWith("video/")) continue;
                            var sb = new StringBuilder("[XrSpatial] codec ").Append(enc ? "ENC " : "DEC ").Append(name).Append(' ').Append(t);
                            if (api >= 29) sb.Append(" hw=").Append(info.Call<bool>("isHardwareAccelerated")).Append(" swOnly=").Append(info.Call<bool>("isSoftwareOnly")).Append(" vendor=").Append(info.Call<bool>("isVendor"));
                            try
                            {
                                using (var caps = info.Call<AndroidJavaObject>("getCapabilitiesForType", t))
                                using (var vc = caps.Call<AndroidJavaObject>("getVideoCapabilities"))
                                {
                                    using (var w = vc.Call<AndroidJavaObject>("getSupportedWidths")) using (var h = vc.Call<AndroidJavaObject>("getSupportedHeights"))
                                        sb.Append(" w=").Append(w.Call<AndroidJavaObject>("getUpper").Call<int>("intValue")).Append(" h=").Append(h.Call<AndroidJavaObject>("getUpper").Call<int>("intValue"));
                                    sb.Append(" instances=").Append(caps.Call<int>("getMaxSupportedInstances"));
                                    if (api >= 30) sb.Append(" lowLatency=").Append(caps.Call<bool>("isFeatureSupported", "low-latency"));
                                }
                            }
                            catch (System.Exception e) { sb.Append(" caps-error=").Append(e.Message); }
                            Debug.Log(sb.ToString());
                        }
                    }
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[XrSpatial] codec probe failed: " + e.Message); }
        }
    }
}
#endif

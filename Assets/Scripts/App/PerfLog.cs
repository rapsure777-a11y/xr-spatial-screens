using UnityEngine;

namespace XrSpatial.App
{
    /// <summary>
    /// Logs the app's own frame rate every 5 s: average, slowest frame and how many frames were slower than 90 Hz allows (11.1 ms). When the head is turned fast and the picture
    /// goes blocky, this tells whether the app missed frames (so the compositor filled in) or kept up (so the blockiness is the screens' own resolution).
    /// </summary>
    /// <summary>Developer switches for finding what costs frame time: words in <c>perf.txt</c> in the app's data folder (nooutline, nopalette, nolayout). Absent file = everything normal.</summary>
    public static class PerfFlags
    {
        static string s_Text;
        public static bool Has(string word)
        {
            if (s_Text == null)
            {
                try { var f = System.IO.Path.Combine(Application.persistentDataPath, "perf.txt"); s_Text = System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f).ToLowerInvariant() : ""; }
                catch { s_Text = ""; }
                if (s_Text.Length > 0) Debug.Log("[XrSpatial] perf flags: " + s_Text.Trim());
            }
            return s_Text.Contains(word);
        }
    }

    public sealed class PerfLog : MonoBehaviour
    {
        float m_Start, m_Worst; int m_Frames, m_Slow;

        void Start() { m_Start = Time.unscaledTime; }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            m_Frames++; if (dt > m_Worst) m_Worst = dt; if (dt > 0.0125f) m_Slow++;
            if (Time.unscaledTime - m_Start < 5f) return;
            float span = Time.unscaledTime - m_Start;
            Debug.Log($"[XrSpatial] frame rate: {m_Frames / span:0.0} fps, slowest frame {m_Worst * 1000f:0} ms, {m_Slow} of {m_Frames} frames slower than 12.5 ms");
            m_Start = Time.unscaledTime; m_Frames = 0; m_Slow = 0; m_Worst = 0f;
        }
    }
}

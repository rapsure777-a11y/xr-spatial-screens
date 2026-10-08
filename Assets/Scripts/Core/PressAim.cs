using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// Where a click should land. Pulling the trigger nudges the controller, so the laser has already moved by the time the press registers. The aim from a moment before
    /// is the one the user meant, as long as it is only a small nudge away; a larger difference is a deliberate movement and the current aim wins.
    /// Positions are normalised source coordinates (0..1), so the tolerance scales with the window.
    /// </summary>
    public sealed class PressAim
    {
        struct Sample { public float time; public Vector2 pos; public int key; }
        readonly Sample[] m_Ring = new Sample[32];
        int m_Next, m_Count;

        /// <summary>Remember where the laser was (call every frame while it hovers a screen; key identifies the screen).</summary>
        public void Record(float time, Vector2 pos, int key)
        {
            m_Ring[m_Next] = new Sample { time = time, pos = pos, key = key };
            m_Next = (m_Next + 1) % m_Ring.Length;
            if (m_Count < m_Ring.Length) m_Count++;
        }

        public void Clear() { m_Count = 0; }

        /// <summary>The aim to press at: the newest remembered position on the same screen that is at least <paramref name="lookback"/> seconds old, if it is within <paramref name="maxNudge"/> of the current one.</summary>
        public Vector2 Choose(float time, Vector2 current, int key, float lookback = 0.07f, float maxNudge = 0.02f)
        {
            for (int i = 1; i <= m_Count; i++)
            {
                var s = m_Ring[(m_Next - i + m_Ring.Length) % m_Ring.Length];
                if (time - s.time < lookback) continue;                                // too recent: still part of the trigger pull
                if (s.key != key) return current;                                      // the laser was on another screen then
                return (s.pos - current).magnitude <= maxNudge ? s.pos : current;
            }
            return current;
        }
    }
}

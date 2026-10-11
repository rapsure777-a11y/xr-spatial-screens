using System;
using System.Collections.Generic;

namespace XrssTransport
{
    /// <summary>
    /// Per-remote-address limits. A key (the remote IP) is locked out for a while after too many authentication/enrollment failures in a window, and a flood of connection attempts is dropped.
    /// The defaults leave normal behaviour alone: a headset that reconnects a few times a minute, or mistypes once, is never affected.
    /// </summary>
    public sealed class RateLimiter
    {
        readonly Func<DateTime> m_Now;
        readonly int m_MaxFailures, m_MaxConnections;
        readonly TimeSpan m_FailureWindow, m_Lockout, m_ConnectionWindow;
        readonly object m_Lock = new object();
        readonly Dictionary<string, Queue<DateTime>> m_Failures = new Dictionary<string, Queue<DateTime>>();
        readonly Dictionary<string, Queue<DateTime>> m_Connections = new Dictionary<string, Queue<DateTime>>();
        readonly Dictionary<string, DateTime> m_LockedUntil = new Dictionary<string, DateTime>();

        public RateLimiter(Func<DateTime> now = null, int maxFailures = 5, TimeSpan? failureWindow = null, TimeSpan? lockout = null, int maxConnections = 20, TimeSpan? connectionWindow = null)
        {
            m_Now = now ?? (() => DateTime.UtcNow); m_MaxFailures = maxFailures; m_FailureWindow = failureWindow ?? TimeSpan.FromSeconds(60);
            m_Lockout = lockout ?? TimeSpan.FromSeconds(60); m_MaxConnections = maxConnections; m_ConnectionWindow = connectionWindow ?? TimeSpan.FromSeconds(10);
        }

        public bool IsLockedOut(string key) { lock (m_Lock) return m_LockedUntil.TryGetValue(key, out var until) && m_Now() < until; }

        /// <summary>Counts a connection attempt; false if the address is locked out or is connecting too fast.</summary>
        public bool AllowConnection(string key)
        {
            lock (m_Lock)
            {
                if (m_LockedUntil.TryGetValue(key, out var until) && m_Now() < until) return false;
                return Count(m_Connections, key, m_ConnectionWindow) <= m_MaxConnections;
            }
        }

        public void Failure(string key)
        {
            lock (m_Lock) { if (Count(m_Failures, key, m_FailureWindow) >= m_MaxFailures) { m_LockedUntil[key] = m_Now() + m_Lockout; m_Failures.Remove(key); } }
        }

        public void Success(string key) { lock (m_Lock) { m_Failures.Remove(key); m_LockedUntil.Remove(key); } }

        int Count(Dictionary<string, Queue<DateTime>> map, string key, TimeSpan window)
        {
            var now = m_Now();
            if (!map.TryGetValue(key, out var q)) map[key] = q = new Queue<DateTime>();
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            q.Enqueue(now);
            return q.Count;
        }
    }
}

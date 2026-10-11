using System;
using System.Threading;

namespace XrssHostApp
{
    /// <summary>
    /// One host app per Windows session. The first instance owns a named mutex and waits on a named event; a second launch finds the mutex taken, sets the event (which asks the first
    /// instance to show its window) and exits without starting a second listener. Both objects live in the per-session <c>Local\</c> namespace.
    /// </summary>
    sealed class SingleInstance : IDisposable
    {
        readonly Mutex m_Mutex;
        readonly EventWaitHandle m_Show;
        readonly RegisteredWaitHandle m_Registered;
        public bool IsFirst { get; }
        public event Action ShowRequested;

        /// <param name="waitMs">How long to wait for a previous instance to let go (used after a crash restart, while the dying process still holds the mutex).</param>
        public SingleInstance(string name = "XrSpatialScreensHost", int waitMs = 0)
        {
            m_Mutex = new Mutex(false, @"Local\" + name + ".instance");
            bool created;
            try { created = m_Mutex.WaitOne(waitMs); }
            catch (AbandonedMutexException) { created = true; }          // the previous instance died holding it
            IsFirst = created;
            m_Show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".show");
            if (IsFirst) m_Registered = ThreadPool.RegisterWaitForSingleObject(m_Show, (_, __) => ShowRequested?.Invoke(), null, Timeout.Infinite, false);
        }

        /// <summary>Called by a second launch: asks the running instance to show itself.</summary>
        public void SignalExisting() { try { m_Show.Set(); } catch (Exception) { } }

        public void Dispose()
        {
            m_Registered?.Unregister(null);
            if (IsFirst) { try { m_Mutex.ReleaseMutex(); } catch (Exception) { } }
            m_Mutex.Dispose(); m_Show.Dispose();
        }
    }
}

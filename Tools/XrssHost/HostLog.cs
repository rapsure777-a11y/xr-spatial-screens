using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XrssTransport;

namespace XrssHostApp
{
    /// <summary>
    /// The host app's transport log: the last lines are kept in memory for the diagnostics page and appended to <c>logs\host.log</c> (rotated at 512 KB). It receives only what the transport
    /// already considers log-safe (short device ids, no secrets, challenges, nonces or pairing codes), so it can be shown and copied as is.
    /// </summary>
    sealed class HostLog : ITransportLog
    {
        const int Keep = 200;
        const long MaxBytes = 512 * 1024;
        readonly object m_Lock = new object();
        readonly LinkedList<string> m_Lines = new LinkedList<string>();
        readonly string m_Path;

        public HostLog(string logDirectory)
        {
            LogDirectory = logDirectory;
            try { Directory.CreateDirectory(logDirectory); m_Path = Path.Combine(logDirectory, "host.log"); } catch (Exception) { m_Path = null; }
        }

        public string LogDirectory { get; }

        public void Info(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (m_Lock)
            {
                m_Lines.AddLast(line);
                while (m_Lines.Count > Keep) m_Lines.RemoveFirst();
                if (m_Path == null) return;
                try
                {
                    var fi = new FileInfo(m_Path);
                    if (fi.Exists && fi.Length > MaxBytes) File.Move(m_Path, m_Path + ".old", true);
                    File.AppendAllText(m_Path, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine);
                }
                catch (Exception) { /* logging must never take the host down */ }
            }
        }

        public IReadOnlyList<string> Recent(int count = 40) { lock (m_Lock) return m_Lines.Skip(Math.Max(0, m_Lines.Count - count)).ToList(); }
    }
}

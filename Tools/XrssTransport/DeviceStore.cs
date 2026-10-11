using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XrssTransport
{
    public sealed class PairedDevice
    {
        public string DeviceId { get; set; }          // 32 hex characters (16 random bytes chosen by the headset)
        public string Label { get; set; }
        public string HostId { get; set; }            // the host identity this pairing belongs to; a different host id never matches
        public string SecretProtected { get; set; }   // base64 of DPAPI(32-byte device secret)
        public DateTime PairedUtc { get; set; }
        public DateTime? LastConnectedUtc { get; set; }
    }

    /// <summary>
    /// The paired-device store: <c>devices.json</c> (version 1). Each entry carries its 32-byte device secret ONLY as a DPAPI-protected blob; the raw secret exists in memory just while a device
    /// is enrolled or its HMAC is checked. Removal deletes the entry (and so the secret); there is no "revoked but kept" state to be resurrected. Writes are atomic (temp file, then replace).
    /// </summary>
    public sealed class DeviceStore
    {
        const int FormatVersion = 1;
        readonly string m_Path;
        readonly IProtector m_Protector;
        readonly Func<DateTime> m_Now;
        readonly object m_Lock = new object();
        readonly List<PairedDevice> m_Devices = new List<PairedDevice>();

        /// <summary>Raised after a device was removed (its live connections must be dropped). Argument: device id hex.</summary>
        public event Action<string> Removed;
        public event Action Changed;

        public DeviceStore(string directory, IProtector protector, Func<DateTime> now = null)
        {
            Directory.CreateDirectory(directory);
            m_Path = Path.Combine(directory, "devices.json");
            m_Protector = protector;
            m_Now = now ?? (() => DateTime.UtcNow);
            Load();
        }

        public int Count { get { lock (m_Lock) return m_Devices.Count; } }

        public IReadOnlyList<PairedDevice> List() { lock (m_Lock) return m_Devices.Select(d => new PairedDevice { DeviceId = d.DeviceId, Label = d.Label, HostId = d.HostId, PairedUtc = d.PairedUtc, LastConnectedUtc = d.LastConnectedUtc, SecretProtected = null }).ToList(); }

        public bool Contains(string deviceIdHex, string hostIdHex) { lock (m_Lock) return Find(deviceIdHex, hostIdHex) != null; }

        /// <summary>Adds or replaces (same device id) a pairing. Callers must have checked the pairing window; the store itself is policy-free.</summary>
        public void Enroll(string deviceIdHex, string label, string hostIdHex, byte[] secret)
        {
            if (secret == null || secret.Length != AuthCrypto.SecretLength) throw new ArgumentException("secret must be 32 bytes");
            string blob = Convert.ToBase64String(m_Protector.Protect(secret));
            lock (m_Lock)
            {
                m_Devices.RemoveAll(d => string.Equals(d.DeviceId, deviceIdHex, StringComparison.OrdinalIgnoreCase));
                m_Devices.Add(new PairedDevice { DeviceId = deviceIdHex.ToUpperInvariant(), Label = label, HostId = hostIdHex.ToUpperInvariant(), SecretProtected = blob, PairedUtc = m_Now() });
                Save();
            }
            Changed?.Invoke();
        }

        /// <summary>Decrypts the secret for a device paired to this host id; null if unknown or paired to another identity. The caller must clear the array after use.</summary>
        public byte[] TryGetSecret(string deviceIdHex, string hostIdHex)
        {
            string blob;
            lock (m_Lock) { var d = Find(deviceIdHex, hostIdHex); blob = d?.SecretProtected; }
            if (blob == null) return null;
            try { return m_Protector.Unprotect(Convert.FromBase64String(blob)); } catch (Exception) { return null; }
        }

        public void MarkConnected(string deviceIdHex)
        {
            lock (m_Lock) { var d = m_Devices.FirstOrDefault(x => string.Equals(x.DeviceId, deviceIdHex, StringComparison.OrdinalIgnoreCase)); if (d == null) return; d.LastConnectedUtc = m_Now(); Save(); }
        }

        public bool Remove(string deviceIdHex)
        {
            bool removed;
            lock (m_Lock) { removed = m_Devices.RemoveAll(d => string.Equals(d.DeviceId, deviceIdHex, StringComparison.OrdinalIgnoreCase)) > 0; if (removed) Save(); }
            if (removed) { Removed?.Invoke(deviceIdHex.ToUpperInvariant()); Changed?.Invoke(); }
            return removed;
        }

        public int RemoveAll()
        {
            List<string> ids;
            lock (m_Lock) { ids = m_Devices.Select(d => d.DeviceId).ToList(); m_Devices.Clear(); Save(); }
            foreach (var id in ids) Removed?.Invoke(id);
            if (ids.Count > 0) Changed?.Invoke();
            return ids.Count;
        }

        PairedDevice Find(string deviceIdHex, string hostIdHex) =>
            m_Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceIdHex, StringComparison.OrdinalIgnoreCase) && string.Equals(d.HostId, hostIdHex, StringComparison.OrdinalIgnoreCase));

        void Load()
        {
            if (!File.Exists(m_Path)) return;
            try
            {
                var file = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(m_Path));
                if (file != null && file.Version == FormatVersion && file.Devices != null) m_Devices.AddRange(file.Devices);
            }
            catch (Exception) { /* unreadable store: start empty; every device must pair again (fail closed) */ }
        }

        void Save()
        {
            string tmp = m_Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileFormat { Version = FormatVersion, Devices = m_Devices }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, m_Path, true);
        }

        sealed class FileFormat { public int Version { get; set; } public List<PairedDevice> Devices { get; set; } }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XrssTransport.Client
{
    /// <summary>
    /// The headset's persistent random identity: a 16-byte application-scoped device id (never a hardware serial or any privacy-sensitive id) and a friendly label.
    /// File <c>device.txt</c> in the secret directory. It changes only through <see cref="Reset"/>, the explicit reset/remove-trust action.
    /// </summary>
    public sealed class HeadsetIdentity
    {
        public byte[] DeviceId { get; private set; }
        public string DeviceIdHex => AuthCrypto.Hex(DeviceId);
        public string Label { get; set; } = "Steam Frame";
        readonly string m_Path;

        HeadsetIdentity(string dir) { m_Path = Path.Combine(dir, "device.txt"); }

        public static HeadsetIdentity LoadOrCreate(string dir)
        {
            Directory.CreateDirectory(dir);
            var id = new HeadsetIdentity(dir);
            if (File.Exists(id.m_Path))
            {
                var kv = KeyValueFile.Read(id.m_Path);
                if (kv.Count > 0 && kv[0].TryGetValue("deviceId", out var hex) && hex.Length == 32)
                {
                    id.DeviceId = AuthCrypto.FromHex(hex);
                    if (kv[0].TryGetValue("label", out var l) && l.Length > 0) id.Label = l;
                    return id;
                }
            }
            id.Reset();
            return id;
        }

        /// <summary>Creates a new random device id. Previously paired PCs no longer recognise this headset.</summary>
        public void Reset()
        {
            DeviceId = AuthCrypto.Random(AuthCrypto.IdLength);
            KeyValueFile.Write(m_Path, new List<Dictionary<string, string>> { new Dictionary<string, string> { { "version", "1" }, { "deviceId", DeviceIdHex }, { "label", Label } } });
        }
    }

    /// <summary>One trusted PC: what the headset needs to recognise it and to authenticate to it.</summary>
    public sealed class PairedHost
    {
        public string HostIdHex;                  // 32 hex: the stable id the PC authenticates with
        public string FingerprintHex;             // 64 hex: SHA-256 of the PC's TLS certificate, authenticated by the pairing code
        public string DeviceIdHex;                // the device id this pairing was made with
        public byte[] Secret;                     // 32 random bytes, the HMAC key; only ever sent to the PC inside the pinned TLS channel at pairing
        public string LastAddress;                // last address that worked
        public List<string> Addresses = new List<string>();   // every address this PC has been seen at (router network, dongle link...); probed at reconnect, fastest first
        public int LastPort;
        public string DisplayName;
        public int AuthVersion = AuthProtocol.AuthVersion;
        public DateTime PairedUtc, LastConnectedUtc;
        public DateTime? RevokedSeenUtc;          // the PC answered "unknown device": do not retry until the user pairs again

        public byte[] HostId => AuthCrypto.FromHex(HostIdHex);
        public byte[] Fingerprint => AuthCrypto.FromHex(FingerprintHex);
        public byte[] DeviceId => AuthCrypto.FromHex(DeviceIdHex);
        public PairedHost Clone() { var c = (PairedHost)MemberwiseClone(); c.Secret = (byte[])Secret?.Clone(); c.Addresses = new List<string>(Addresses); return c; }
    }

    /// <summary>
    /// Paired PCs, one block per host in <c>paired-hosts.txt</c> (key=value lines; the secret as base64) in the app-private secret directory. Honest protection statement: this is
    /// app-private Android storage (the app's internal files directory, unreadable to other apps and to adb on a non-debuggable build), not the Android Keystore. Writes are atomic.
    /// A host record is only ever written by <see cref="Save"/> after the PC returned a successful enrollment.
    /// </summary>
    public sealed class PairedHostStore
    {
        readonly string m_Path;
        readonly object m_Lock = new object();
        readonly List<PairedHost> m_Hosts = new List<PairedHost>();

        public PairedHostStore(string dir) { Directory.CreateDirectory(dir); m_Path = Path.Combine(dir, "paired-hosts.txt"); Load(); }

        public int Count { get { lock (m_Lock) return m_Hosts.Count; } }

        /// <summary>The host to connect to: the one used or paired most recently.</summary>
        public PairedHost Primary
        {
            get
            {
                lock (m_Lock)
                {
                    PairedHost best = null;
                    foreach (var h in m_Hosts) if (best == null || Newest(h) > Newest(best)) best = h;
                    return best?.Clone();
                }
            }
        }

        static DateTime Newest(PairedHost h) => h.LastConnectedUtc > h.PairedUtc ? h.LastConnectedUtc : h.PairedUtc;

        public PairedHost Get(string hostIdHex) { lock (m_Lock) return m_Hosts.Find(h => string.Equals(h.HostIdHex, hostIdHex, StringComparison.OrdinalIgnoreCase))?.Clone(); }

        /// <summary>Adds or replaces the record for this host id.</summary>
        public void Save(PairedHost host)
        {
            lock (m_Lock) { m_Hosts.RemoveAll(h => string.Equals(h.HostIdHex, host.HostIdHex, StringComparison.OrdinalIgnoreCase)); m_Hosts.Add(host.Clone()); Write(); }
        }

        public void Update(string hostIdHex, Action<PairedHost> change)
        {
            lock (m_Lock) { var h = m_Hosts.Find(x => string.Equals(x.HostIdHex, hostIdHex, StringComparison.OrdinalIgnoreCase)); if (h == null) return; change(h); Write(); }
        }

        public bool Remove(string hostIdHex) { lock (m_Lock) { bool r = m_Hosts.RemoveAll(h => string.Equals(h.HostIdHex, hostIdHex, StringComparison.OrdinalIgnoreCase)) > 0; if (r) Write(); return r; } }

        public void Clear() { lock (m_Lock) { m_Hosts.Clear(); Write(); } }

        void Load()
        {
            if (!File.Exists(m_Path)) return;
            try
            {
                foreach (var d in KeyValueFile.Read(m_Path))
                {
                    if (!d.TryGetValue("hostId", out var hid) || !d.TryGetValue("fingerprint", out var fp) || !d.TryGetValue("deviceId", out var did) || !d.TryGetValue("secret", out var sec)) continue;
                    var h = new PairedHost { HostIdHex = hid, FingerprintHex = fp, DeviceIdHex = did, Secret = Convert.FromBase64String(sec) };
                    if (h.Secret.Length != AuthCrypto.SecretLength || hid.Length != 32 || fp.Length != 64 || did.Length != 32) continue;
                    d.TryGetValue("address", out h.LastAddress); if (d.TryGetValue("addresses", out var al) && al.Length > 0) h.Addresses = new List<string>(al.Split(';')); if (d.TryGetValue("port", out var p)) int.TryParse(p, out h.LastPort);
                    d.TryGetValue("name", out h.DisplayName); if (d.TryGetValue("authVersion", out var av)) int.TryParse(av, out h.AuthVersion);
                    h.PairedUtc = ParseDate(d, "paired"); h.LastConnectedUtc = ParseDate(d, "lastConnected");
                    if (d.TryGetValue("revokedSeen", out var rs) && rs.Length > 0) h.RevokedSeenUtc = ParseDate(d, "revokedSeen");
                    m_Hosts.Add(h);
                }
            }
            catch (Exception) { m_Hosts.Clear(); }                                      // unreadable: start empty (fail closed, pair again)
        }

        static DateTime ParseDate(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var s) && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;

        void Write()
        {
            var blocks = new List<Dictionary<string, string>>();
            foreach (var h in m_Hosts)
                blocks.Add(new Dictionary<string, string>
                {
                    { "hostId", h.HostIdHex }, { "fingerprint", h.FingerprintHex }, { "deviceId", h.DeviceIdHex }, { "secret", Convert.ToBase64String(h.Secret) },
                    { "address", h.LastAddress ?? "" }, { "addresses", string.Join(";", h.Addresses) }, { "port", h.LastPort.ToString() }, { "name", h.DisplayName ?? "" }, { "authVersion", h.AuthVersion.ToString() },
                    { "paired", h.PairedUtc.ToString("o") }, { "lastConnected", h.LastConnectedUtc.ToString("o") }, { "revokedSeen", h.RevokedSeenUtc?.ToString("o") ?? "" },
                });
            KeyValueFile.Write(m_Path, blocks);
        }
    }

    /// <summary>The tiny storage format: blocks of key=value lines separated by a line "[host]"; first line "version=1". Values never contain newlines (they are hex, base64, ISO dates, or sanitised names).</summary>
    static class KeyValueFile
    {
        public static List<Dictionary<string, string>> Read(string path)
        {
            var list = new List<Dictionary<string, string>>();
            Dictionary<string, string> cur = null;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line == "[host]") { cur = new Dictionary<string, string>(); list.Add(cur); continue; }
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (cur == null) continue;                                                   // header lines before the first [host]
                cur[k] = v;
            }
            return list;
        }

        public static void Write(string path, List<Dictionary<string, string>> blocks)
        {
            var sb = new StringBuilder();
            sb.Append("version=1\n");
            foreach (var b in blocks) { sb.Append("[host]\n"); foreach (var kv in b) sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append('\n'); }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}

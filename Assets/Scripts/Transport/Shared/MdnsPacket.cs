using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace XrssTransport
{
    /// <summary>One DNS resource record, decoded as far as service discovery needs.</summary>
    public sealed class MdnsRecord
    {
        public string Name;
        public int Type;                       // 1 A, 12 PTR, 16 TXT, 33 SRV
        public int Ttl;
        public string Ptr;                     // PTR target
        public string SrvTarget; public int SrvPort;
        public Dictionary<string, string> Txt; // TXT key=value pairs (keys lower-cased)
        public IPAddress Address;              // A
    }

    public sealed class MdnsMessage
    {
        public bool IsResponse;
        public List<string> Questions = new List<string>();
        public List<MdnsRecord> Records = new List<MdnsRecord>();     // answers, authority and additional records together
    }

    /// <summary>A PC that advertises the XR Spatial Screens service on the local network.</summary>
    public sealed class DiscoveredPc
    {
        public string Instance;                // e.g. "My PC._xrss._tcp.local"
        public string HostName;                // SRV target, e.g. "my-pc.local"
        public int Port;
        public string HostIdHex;               // TXT id=, the stable host id (may be null for foreign advertisers)
        public string DisplayName;             // TXT name=, else the instance label
        public List<IPAddress> Addresses = new List<IPAddress>();
        public override string ToString() => (DisplayName ?? Instance) + " " + string.Join(",", Addresses) + ":" + Port;
    }

    /// <summary>
    /// Minimal multicast-DNS wire format for the one service we use, <c>_xrss._tcp.local</c>. SHARED SOURCE: the host builds answers and the headset parses them with this same file.
    /// The parser handles name compression and tolerates garbage (it never throws on malformed input; it returns what it could read).
    /// </summary>
    public static class MdnsPacket
    {
        public const string Service = "_xrss._tcp.local";
        public const int TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

        // ------------------------------------------------------------------ building

        /// <summary>A PTR question for the service. <paramref name="unicastResponse"/> sets the QU bit so a responder may answer straight to the sender (needed when we could not bind port 5353).</summary>
        public static byte[] BuildQuery(bool unicastResponse)
        {
            var b = new List<byte>();
            U16(b, 0); U16(b, 0); U16(b, 1); U16(b, 0); U16(b, 0); U16(b, 0);
            Name(b, Service); U16(b, TypePtr); U16(b, unicastResponse ? 0x8001 : 1);
            return b.ToArray();
        }

        /// <summary>The host's answer: PTR in the answer section; SRV, TXT and one A record per address in the additional section. <paramref name="ttl"/> 0 is a goodbye.</summary>
        public static byte[] BuildResponse(string instanceLabel, string hostName, int port, IReadOnlyList<KeyValuePair<string, string>> txt, IReadOnlyList<IPAddress> addresses, int ttl = 120)
        {
            string inst = instanceLabel + "." + Service;
            string host = hostName + ".local";
            var b = new List<byte>();
            U16(b, 0); U16(b, 0x8400); U16(b, 0); U16(b, 1); U16(b, 0); U16(b, 2 + addresses.Count);
            var r = new List<byte>(); Name(r, inst); Record(b, Service, TypePtr, false, ttl, r.ToArray());
            r = new List<byte>(); U16(r, 0); U16(r, 0); U16(r, port); Name(r, host); Record(b, inst, TypeSrv, true, ttl, r.ToArray());
            r = new List<byte>();
            foreach (var kv in txt) { var s = Encoding.UTF8.GetBytes(kv.Key + "=" + kv.Value); if (s.Length > 255) continue; r.Add((byte)s.Length); r.AddRange(s); }
            if (r.Count == 0) r.Add(0);
            Record(b, inst, TypeTxt, true, ttl, r.ToArray());
            foreach (var a in addresses) Record(b, host, TypeA, true, ttl, a.GetAddressBytes());
            return b.ToArray();
        }

        static void Record(List<byte> b, string name, int type, bool cacheFlush, int ttl, byte[] rdata)
        {
            Name(b, name); U16(b, type); U16(b, (cacheFlush ? 0x8000 : 0) | 1); U16(b, ttl >> 16); U16(b, ttl & 0xFFFF); U16(b, rdata.Length); b.AddRange(rdata);
        }

        static void Name(List<byte> b, string name)
        {
            foreach (var label in name.Split('.')) { var x = Encoding.UTF8.GetBytes(label); if (x.Length == 0 || x.Length > 63) throw new ArgumentException("bad DNS label"); b.Add((byte)x.Length); b.AddRange(x); }
            b.Add(0);
        }

        static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

        // ------------------------------------------------------------------ parsing

        public static MdnsMessage Parse(byte[] d, int length = -1)
        {
            var m = new MdnsMessage();
            if (d == null) return m;
            int n = length < 0 ? d.Length : Math.Min(length, d.Length);
            if (n < 12) return m;
            try
            {
                m.IsResponse = (d[2] & 0x80) != 0;
                int qd = d[4] << 8 | d[5], an = d[6] << 8 | d[7], ns = d[8] << 8 | d[9], ar = d[10] << 8 | d[11];
                int p = 12;
                for (int i = 0; i < qd; i++) { m.Questions.Add(ReadName(d, n, ref p)); p += 4; if (p > n) return m; }
                int total = an + ns + ar;
                for (int i = 0; i < total && p < n; i++)
                {
                    string name = ReadName(d, n, ref p);
                    if (p + 10 > n) break;
                    int type = d[p] << 8 | d[p + 1]; int ttl = d[p + 4] << 24 | d[p + 5] << 16 | d[p + 6] << 8 | d[p + 7]; int rdlen = d[p + 8] << 8 | d[p + 9];
                    p += 10;
                    if (p + rdlen > n) break;
                    var rec = new MdnsRecord { Name = name, Type = type, Ttl = ttl };
                    int q = p;
                    if (type == TypePtr) rec.Ptr = ReadName(d, n, ref q);
                    else if (type == TypeSrv && rdlen >= 7) { rec.SrvPort = d[p + 4] << 8 | d[p + 5]; q = p + 6; rec.SrvTarget = ReadName(d, n, ref q); }
                    else if (type == TypeA && rdlen == 4) rec.Address = new IPAddress(new[] { d[p], d[p + 1], d[p + 2], d[p + 3] });
                    else if (type == TypeTxt)
                    {
                        rec.Txt = new Dictionary<string, string>();
                        int t = p;
                        while (t < p + rdlen) { int l = d[t++]; if (l == 0 || t + l > p + rdlen) break; string s = Encoding.UTF8.GetString(d, t, l); t += l; int eq = s.IndexOf('='); rec.Txt[(eq < 0 ? s : s.Substring(0, eq)).ToLowerInvariant()] = eq < 0 ? "" : s.Substring(eq + 1); }
                    }
                    m.Records.Add(rec);
                    p += rdlen;
                }
            }
            catch (Exception) { /* malformed packet: keep what was read */ }
            return m;
        }

        static string ReadName(byte[] d, int n, ref int p)
        {
            var sb = new StringBuilder();
            int pos = p; bool jumped = false; int hops = 0;
            while (true)
            {
                if (pos >= n) throw new FormatException("name past end");
                int l = d[pos];
                if (l == 0) { pos++; break; }
                if ((l & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= n) throw new FormatException("bad pointer");
                    int target = (l & 0x3F) << 8 | d[pos + 1];
                    if (!jumped) { p = pos + 2; jumped = true; }
                    if (++hops > 20 || target >= n) throw new FormatException("pointer loop");
                    pos = target; continue;
                }
                if ((l & 0xC0) != 0) throw new FormatException("bad label");
                pos++;
                if (pos + l > n) throw new FormatException("label past end");
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(d, pos, l)); pos += l;
            }
            if (!jumped) p = pos;
            return sb.ToString();
        }
    }

    /// <summary>Collects mDNS answers over time and assembles them into PCs. Tolerates duplicates, answers split over several packets, and several addresses per PC.</summary>
    public sealed class MdnsCollector
    {
        readonly HashSet<string> m_Instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (string target, int port)> m_Srv = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, string>> m_Txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<IPAddress>> m_Addr = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);

        public void Add(MdnsMessage m)
        {
            if (m == null || !m.IsResponse) return;
            foreach (var r in m.Records)
            {
                if (r.Ttl == 0 && r.Type == MdnsPacket.TypePtr) { m_Instances.Remove(r.Ptr ?? ""); continue; }              // goodbye
                switch (r.Type)
                {
                    case MdnsPacket.TypePtr: if (string.Equals(r.Name, MdnsPacket.Service, StringComparison.OrdinalIgnoreCase) && r.Ptr != null) m_Instances.Add(r.Ptr); break;
                    case MdnsPacket.TypeSrv: if (r.SrvTarget != null) m_Srv[r.Name] = (r.SrvTarget, r.SrvPort); break;
                    case MdnsPacket.TypeTxt: if (r.Txt != null) m_Txt[r.Name] = r.Txt; break;
                    case MdnsPacket.TypeA:
                        if (r.Address == null) break;
                        if (!m_Addr.TryGetValue(r.Name, out var l)) m_Addr[r.Name] = l = new List<IPAddress>();
                        if (!l.Contains(r.Address)) l.Add(r.Address);
                        break;
                }
            }
        }

        public List<DiscoveredPc> Results()
        {
            var list = new List<DiscoveredPc>();
            foreach (var inst in m_Instances)
            {
                if (!m_Srv.TryGetValue(inst, out var srv)) continue;
                var pc = new DiscoveredPc { Instance = inst, HostName = srv.target, Port = srv.port };
                if (m_Txt.TryGetValue(inst, out var txt))
                {
                    if (txt.TryGetValue("id", out var id) && id.Length == 32) pc.HostIdHex = id.ToUpperInvariant();
                    if (txt.TryGetValue("name", out var nm) && nm.Length > 0) pc.DisplayName = nm;
                }
                if (pc.DisplayName == null) { int dot = inst.IndexOf("._xrss", StringComparison.OrdinalIgnoreCase); pc.DisplayName = dot > 0 ? inst.Substring(0, dot) : inst; }
                if (m_Addr.TryGetValue(srv.target, out var addrs)) pc.Addresses.AddRange(addrs);
                if (pc.Addresses.Count > 0) list.Add(pc);                                  // no address yet: cannot connect, wait for a later packet
            }
            return list;
        }
    }
}

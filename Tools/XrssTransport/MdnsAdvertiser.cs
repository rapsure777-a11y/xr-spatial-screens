using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace XrssTransport
{
    /// <summary>
    /// Announces this PC as <c>_xrss._tcp</c> on the local network so a headset can find it (TXT: id = host id, name = display name, v = 1). Runs only while the secure listener is bound,
    /// so a PC that is not pairing and has nothing paired is invisible. Answers queries both by multicast and straight to the asker (a headset that could not bind port 5353 asks with the unicast bit).
    /// Wire format: <see cref="MdnsPacket"/> (shared source with the headset).
    /// </summary>
    public sealed class MdnsAdvertiser : IDisposable
    {
        readonly string m_Label, m_Host, m_Id, m_Name;
        readonly int m_Port;
        readonly IReadOnlyList<IPAddress> m_Addresses;
        readonly ITransportLog m_Log;
        UdpClient m_Udp;
        Thread m_Listen, m_Announce;
        volatile bool m_Stop;
        static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
        static readonly IPEndPoint GroupEndPoint = new IPEndPoint(Group, 5353);

        public MdnsAdvertiser(string displayName, string hostIdHex, int port, IReadOnlyList<IPAddress> addresses, ITransportLog log = null)
        {
            m_Name = displayName; m_Id = hostIdHex; m_Port = port; m_Addresses = addresses; m_Log = log ?? new NullTransportLog();
            string safe = Sanitize(displayName);
            m_Label = safe; m_Host = "xrss-" + safe.ToLowerInvariant().Replace(' ', '-') + "-" + hostIdHex.Substring(0, 4).ToLowerInvariant();
        }

        public bool Running => m_Udp != null && !m_Stop;

        public void Start()
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var a in m_Addresses) { try { udp.JoinMulticastGroup(Group, a); } catch (Exception e) { m_Log.Info("mdns: could not join on " + a + ": " + e.Message); } }
            m_Udp = udp; m_Stop = false;
            m_Listen = new Thread(ListenLoop) { IsBackground = true, Name = "xrss-mdns" }; m_Listen.Start();
            m_Announce = new Thread(AnnounceLoop) { IsBackground = true, Name = "xrss-mdns-announce" }; m_Announce.Start();
            m_Log.Info("mdns: advertising " + m_Label + " on " + string.Join(", ", m_Addresses) + ", port " + m_Port);
        }

        void ListenLoop()
        {
            while (!m_Stop)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = m_Udp.Receive(ref from);
                    var msg = MdnsPacket.Parse(data);
                    if (msg.IsResponse) continue;
                    bool ours = false; foreach (var q in msg.Questions) if (string.Equals(q, MdnsPacket.Service, StringComparison.OrdinalIgnoreCase)) ours = true;
                    if (!ours) continue;
                    byte[] reply = Response(120);
                    SendMulticast(reply);
                    try { m_Udp.Send(reply, reply.Length, from); } catch { }                // unicast to the asker too
                }
                catch (Exception) { if (m_Stop) return; Thread.Sleep(50); }
            }
        }

        void AnnounceLoop()
        {
            int n = 0;
            while (!m_Stop)
            {
                SendMulticast(Response(120));
                for (int i = 0; i < (n++ < 2 ? 10 : 600) && !m_Stop; i++) Thread.Sleep(100);        // twice quickly, then once a minute
            }
        }

        byte[] Response(int ttl) => MdnsPacket.BuildResponse(m_Label, m_Host, m_Port,
            new[] { new KeyValuePair<string, string>("v", "1"), new KeyValuePair<string, string>("id", m_Id), new KeyValuePair<string, string>("name", m_Name) }, m_Addresses, ttl);

        void SendMulticast(byte[] packet)
        {
            var udp = m_Udp; if (udp == null) return;
            foreach (var a in m_Addresses)
            {
                try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes()); udp.Send(packet, packet.Length, GroupEndPoint); } catch (Exception) { }
            }
        }

        public void Stop()
        {
            if (m_Udp == null) return;
            try { SendMulticast(Response(0)); } catch { }                                           // goodbye
            m_Stop = true;
            try { m_Udp.Close(); } catch { }
            m_Udp = null;
            m_Log.Info("mdns: stopped advertising");
        }

        public void Dispose() => Stop();

        static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "PC") if (char.IsLetterOrDigit(c) || c == ' ' || c == '-') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "PC" : r.Length > 40 ? r.Substring(0, 40) : r;
        }
    }
}

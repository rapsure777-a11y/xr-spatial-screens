using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace XrssTransport.Client
{
    /// <summary>Finds PCs on the local network. An interface so connection logic can be tested with scripted answers.</summary>
    public interface IDiscovery
    {
        /// <summary>Searches for up to <paramref name="timeoutMs"/> (stops early as soon as <paramref name="enough"/> returns true for the current results). Never throws.</summary>
        List<DiscoveredPc> Discover(int timeoutMs, Func<List<DiscoveredPc>, bool> enough);
    }

    /// <summary>
    /// mDNS service discovery for <c>_xrss._tcp</c>, as proven on the Steam Frame by the spike: take the Wi-Fi multicast lock, join 224.0.0.251 on port 5353 (on every local IPv4 interface),
    /// send PTR queries, collect PTR/SRV/TXT/A answers. If port 5353 cannot be bound, an ephemeral port is used with the unicast-response bit so PCs answer straight to us.
    /// Duplicate answers and several addresses per PC are merged (<see cref="MdnsCollector"/>). Blocking: always call it from a worker thread, never the Unity main thread.
    /// </summary>
    public sealed class MdnsDiscovery : IDiscovery
    {
        readonly IClientEnv m_Env;
        public MdnsDiscovery(IClientEnv env) { m_Env = env; }

        public List<DiscoveredPc> Discover(int timeoutMs, Func<List<DiscoveredPc>, bool> enough)
        {
            var collector = new MdnsCollector();
            UdpClient udp = null;
            IDisposable mlock = null;
            try
            {
                mlock = m_Env.AcquireMulticastLock();
                bool shared = true;
                udp = new UdpClient(AddressFamily.InterNetwork);
                try { udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353)); }
                catch (Exception) { try { udp.Close(); } catch { } udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); shared = false; }
                var group = IPAddress.Parse("224.0.0.251");
                var locals = LocalAddresses();
                int joined = 0;
                foreach (var a in locals) { try { udp.JoinMulticastGroup(group, a); joined++; } catch (Exception) { } }
                if (joined == 0) { try { udp.JoinMulticastGroup(group); } catch (Exception) { } }
                udp.Client.ReceiveTimeout = 250;
                byte[] query = MdnsPacket.BuildQuery(!shared);
                var dest = new IPEndPoint(group, 5353);
                var sw = Stopwatch.StartNew(); long nextQuery = 0;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (sw.ElapsedMilliseconds >= nextQuery)
                    {
                        nextQuery = sw.ElapsedMilliseconds + 900;
                        if (locals.Count == 0) { try { udp.Send(query, query.Length, dest); } catch { } }
                        foreach (var a in locals)
                        {
                            try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes()); udp.Send(query, query.Length, dest); } catch (Exception) { }
                        }
                    }
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref from);
                        collector.Add(MdnsPacket.Parse(data));
                        if (enough != null && enough(collector.Results())) break;
                    }
                    catch (SocketException) { /* receive timeout: loop */ }
                }
            }
            catch (Exception e) { m_Env.Log("discovery: " + e.GetType().Name + ": " + e.Message); }
            finally { try { udp?.Close(); } catch { } try { mlock?.Dispose(); } catch { } }
            return collector.Results();
        }

        static List<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) && !ua.Address.ToString().StartsWith("169.254.")) list.Add(ua.Address);
                }
            }
            catch (Exception) { }
            return list;
        }
    }
}

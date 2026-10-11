using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XrssTransport
{
    /// <summary>The local addresses the consumer listener may bind: up, non-loopback IPv4 addresses on private (RFC 1918) networks. Never a public address, never 0.0.0.0.</summary>
    public static class LanInterfaces
    {
        public static List<IPAddress> PrivateIPv4()
        {
            var list = new List<IPAddress>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address)) list.Add(ua.Address);
            }
            return list;
        }

        public static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
    }
}

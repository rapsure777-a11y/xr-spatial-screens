using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XrssHostApp
{
    /// <summary>How the connected headset reaches this PC. Deliberately conservative: only an adapter that identifies itself as the Valve/Steam Frame dongle is called "Dongle"; everything else is just "Wi-Fi / LAN".</summary>
    sealed class RouteInfo
    {
        public string Kind;         // "Dongle", "Wi-Fi / LAN", "Developer (adb)", or null when unknown
        public string Adapter;      // the PC's network adapter the connection arrived on, for diagnostics
        public string Local;
    }

    static class Route
    {
        public sealed class Adapter { public string Address, Name, Description; }

        public static IEnumerable<Adapter> SystemAdapters()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return new Adapter { Address = ua.Address.ToString(), Name = ni.Name, Description = ni.Description };
        }

        /// <param name="localAddress">The PC-side address of the connection.</param>
        public static RouteInfo Classify(string localAddress, Func<IEnumerable<Adapter>> adapters = null)
        {
            if (string.IsNullOrEmpty(localAddress)) return new RouteInfo();
            Adapter a = null;
            try { a = (adapters ?? SystemAdapters)().FirstOrDefault(x => x.Address == localAddress); } catch (Exception) { }
            if (a == null) return new RouteInfo { Kind = null, Local = localAddress };
            string d = ((a.Description ?? "") + " " + (a.Name ?? "")).ToLowerInvariant();
            bool dongle = d.Contains("valve") || d.Contains("steam frame") || d.Contains("steamframe");
            return new RouteInfo { Kind = dongle ? "Dongle" : "Wi-Fi / LAN", Adapter = a.Name, Local = localAddress };
        }
    }
}

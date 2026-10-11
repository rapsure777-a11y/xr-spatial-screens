using System;
using System.Collections.Generic;
using XrssTransport.Client;

namespace XrSpatial.App
{
    /// <summary>One button on the PC connection page.</summary>
    public sealed class ConnAction { public string Label; public Action Do; public bool Highlight; }

    /// <summary>What the connection page shows right now: plain words only (no certificates, fingerprints or secrets), a big code when one must be compared, and the buttons that make sense.</summary>
    public sealed class ConnView
    {
        public string Title = "", Body = "", Code;
        public bool Keypad;                              // show the address keypad
        public string Typed = "";
        public readonly List<ConnAction> Actions = new List<ConnAction>();
    }

    /// <summary>Page-local choices (not connection state): are we typing an address, did the user ask to pair a PC.</summary>
    public sealed class ConnectionPageState
    {
        public bool Typing, PairMode;
        public string Typed = "";
        public int Version;
        public void Touch() { Version++; }
    }

    /// <summary>
    /// Decides what the PC connection page says and offers for a <see cref="LinkSnapshot"/>. Pure logic over <see cref="PcLink"/>: the palette only draws what this returns, so the pairing flow can be
    /// tested without a headset and the rest of the app never touches the pairing types.
    /// </summary>
    public static class ConnectionPage
    {
        public static string ShortState(LinkSnapshot s)
        {
            if (s.Mode == LinkMode.Dev) return "Dev (adb)";
            switch (s.State)
            {
                case LinkState.Connected: return "Connected";
                case LinkState.Reconnecting: return "Reconnecting";
                case LinkState.Connecting: return "Connecting";
                case LinkState.NotPaired: return "Not paired";
                case LinkState.Pairing: return "Pairing";
                case LinkState.Error: return s.Error == LinkError.Removed ? "Pair again" : "Problem";
                default: return s.State.ToString();
            }
        }

        public static ConnView Build(PcLink link, ConnectionPageState ui, Action goBack) => Build(link.Snapshot, link, ui, goBack);

        /// <summary>Builds the view for a given snapshot (tests pass synthetic ones; the actions still act on <paramref name="link"/>).</summary>
        public static ConnView Build(LinkSnapshot s, PcLink link, ConnectionPageState ui, Action goBack)
        {
            var v = new ConnView();
            void Act(string label, Action a, bool hi = false) { v.Actions.Add(new ConnAction { Label = label, Do = () => { a(); ui.Touch(); }, Highlight = hi }); }

            if (s.Mode == LinkMode.Dev)
            {
                v.Title = "PC connection: Dev (adb)";
                v.Body = "This headset is using the developer connection through adb. Switch to Paired PC to connect to a PC over your network.";
                Act("Use Paired PC", () => { ui.PairMode = false; link.SetMode(LinkMode.PairedLan); }, true);
                return v;
            }

            if (ui.Typing)
            {
                v.Title = "Type the PC's address";
                v.Body = "Example: 192.168.1.20  (port 5610 is used unless you add :port)";
                v.Keypad = true; v.Typed = ui.Typed;
                return v;
            }

            // pairing in progress: the one thing to do right now
            if (s.State == LinkState.Pairing)
            {
                switch (s.Step)
                {
                    case PairStep.ShowCode:
                        v.Title = "Compare the codes"; v.Code = Spaced(s.Code); v.Body = "Does your PC show the same six digits?";
                        Act("Same code", () => link.AnswerCode(true), true); Act("Doesn't match", () => link.AnswerCode(false)); return v;
                    case PairStep.WaitingForPc:
                        v.Title = "Confirm on your PC"; v.Code = Spaced(s.Code); v.Body = "Confirm the same code on your PC to finish.";
                        Act("Cancel", link.CancelPairing); return v;
                    case PairStep.Complete:
                        v.Title = "Paired"; v.Body = s.Text; Act("Done", () => { ui.PairMode = false; goBack(); }, true); return v;
                    default:
                        v.Title = "Pairing"; v.Body = s.Text; Act("Cancel", link.CancelPairing); return v;
                }
            }

            if (s.State == LinkState.Connected)
            {
                v.Title = "Connected"; v.Body = (s.PcName ?? "Your PC") + (string.IsNullOrEmpty(s.ConnectedAddress) ? "" : "\n" + s.ConnectedAddress);
                Act("Forget this PC", () => { link.Forget(); ui.PairMode = true; });
                Act("Use Dev connection", () => link.SetMode(LinkMode.Dev));
                return v;
            }

            bool needPairing = !s.Paired || ui.PairMode || (s.State == LinkState.Error && (s.Error == LinkError.Removed || s.Error == LinkError.WrongPc));
            if (needPairing)
            {
                v.Title = s.Paired && s.State == LinkState.Error ? (s.Error == LinkError.Removed ? "This PC removed this headset" : "Different PC") : "Pair with your PC";
                v.Body = (s.State == LinkState.Error || s.State == LinkState.Reconnecting) && !string.IsNullOrEmpty(s.Text) ? s.Text + "\n" : "";
                v.Body += s.Found.Count == 0 ? (s.Searching ? "Looking for PCs on your network..." : "On your PC, open pairing. Then search for it here.") : "Choose your PC:";
                foreach (var pc in s.Found)
                {
                    var one = pc; string addr = pc.Addresses.Count > 0 ? pc.Addresses[0].ToString() : "";
                    Act("Pair with " + Trim(pc.DisplayName, 22) + "  " + addr, () => { link.BeginPairing(one); }, true);
                }
                Act(s.Searching ? "Searching..." : "Search again", () => link.StartSearch());
                Act("Type address...", () => { ui.Typing = true; ui.Typed = s.ManualAddress ?? ""; });
                if (s.Paired) Act("Forget this PC", () => { link.Forget(); });
                if (s.Paired && s.Error != LinkError.Removed && s.Error != LinkError.WrongPc) Act("Back to reconnecting", () => { ui.PairMode = false; });
                Act("Use Dev connection", () => link.SetMode(LinkMode.Dev));
                return v;
            }

            // paired and not connected
            v.Title = s.State == LinkState.Reconnecting ? "Reconnecting" : s.State == LinkState.Connecting ? "Connecting" : "Can't reach your PC";
            v.Body = (s.PcName ?? "Your PC") + "\n" + s.Text;
            Act("Retry now", link.RetryNow, true);
            Act("Pair again", () => { ui.PairMode = true; link.StartSearch(); });
            Act("Type address...", () => { ui.Typing = true; ui.Typed = s.ManualAddress ?? ""; });
            Act("Forget this PC", () => { link.Forget(); });
            Act("Use Dev connection", () => link.SetMode(LinkMode.Dev));
            return v;
        }

        /// <summary>Called when the user presses a keypad key or an action in typing mode.</summary>
        public static void Key(ConnectionPageState ui, string key)
        {
            if (key == "<") { if (ui.Typed.Length > 0) ui.Typed = ui.Typed.Substring(0, ui.Typed.Length - 1); }
            else if (ui.Typed.Length < 24) ui.Typed += key;
            ui.Touch();
        }

        static string Spaced(string code) => code != null && code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code ?? "";
        static string Trim(string s, int n) => s == null ? "PC" : s.Length <= n ? s : s.Substring(0, n - 1) + "...";
    }
}

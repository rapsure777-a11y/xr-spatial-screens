using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using XrssHostApp;

public class HeadsetLauncherTests : IDisposable
{
    readonly string m_Dir = Path.Combine(Path.GetTempPath(), "xrss-launch-" + Guid.NewGuid().ToString("N"));
    readonly string m_App;
    readonly List<string> m_Calls = new List<string>();

    public HeadsetLauncherTests()
    {
        m_App = Path.Combine(m_Dir, "app"); Directory.CreateDirectory(m_App);
        File.WriteAllText(Path.Combine(m_App, "adb.exe"), "fake");            // FindAdb looks next to the app first
    }
    public void Dispose() { try { Directory.Delete(m_Dir, true); } catch { } }

    HeadsetLauncher Make(Func<string, int, (int, string)> adb)
    {
        var l = new HeadsetLauncher(Path.Combine(m_Dir, "state"), m_App);
        l.Run = (exe, args, ms) => { m_Calls.Add(args); return adb(args, ms); };
        return l;
    }

    [Fact]
    public void The_dongle_address_is_always_a_candidate_and_remembered_ones_come_first()
    {
        var l = Make((a, m) => (0, ""));
        Assert.Equal(new[] { HeadsetLauncher.DongleAddress }, l.Candidates());
        l.Remember("10.0.0.171"); l.Remember("10.35.78.1"); l.Remember("192.168.1.20");
        var c = l.Candidates();
        Assert.Equal("10.35.78.1", c[0]);                                       // the dongle link is tried first
        Assert.Contains("10.0.0.171", c); Assert.Contains("192.168.1.20", c);
        Assert.Equal(c.Count, c.Distinct().Count());
    }

    [Fact]
    public void Junk_addresses_are_ignored_and_mapped_ipv4_is_cleaned()
    {
        var l = Make((a, m) => (0, ""));
        l.Remember(null); l.Remember("?"); l.Remember("not an address"); l.Remember("fe80::1");
        l.Remember("::ffff:10.0.0.171");
        Assert.Equal(new[] { "10.0.0.171", HeadsetLauncher.DongleAddress }, l.Candidates());
    }

    [Fact]
    public void Missing_adb_gives_a_plain_message()
    {
        var l = new HeadsetLauncher(Path.Combine(m_Dir, "state"), Path.Combine(m_Dir, "nowhere"));
        Environment.SetEnvironmentVariable("XRSS_ADB", null);
        var r = l.Launch();
        if (l.FindAdb() == null) { Assert.False(r.Ok); Assert.Contains("adb.exe", r.Message); }          // a dev PC may have adb elsewhere; then there is nothing to check
    }

    [Fact]
    public void Unreachable_headset_tries_every_address_and_says_so()
    {
        var l = Make((a, m) => (0, "failed to connect to '" + a + "'"));
        l.Remember("10.0.0.171");
        var r = l.Launch();
        Assert.False(r.Ok);
        Assert.Contains("Couldn't reach the headset", r.Message);
        Assert.Equal(2, m_Calls.Count(c => c.StartsWith("connect ")));           // the remembered address and the dongle
        Assert.DoesNotContain(m_Calls, c => c.Contains("am start"));
    }

    [Fact]
    public void Normal_start_connects_launches_and_confirms_the_app_is_running()
    {
        int pidChecks = 0;
        var l = Make((a, m) =>
        {
            if (a.StartsWith("connect ")) return (0, "connected to " + a.Substring(8));
            if (a.Contains("pidof")) return (0, ++pidChecks >= 2 ? "6358\r\n" : "");
            if (a.Contains("am start")) return (0, "Starting: Intent { cmp=" + HeadsetLauncher.Package + "/" + HeadsetLauncher.Activity + " }");
            return (1, "unexpected " + a);
        });
        var r = l.Launch();
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Started", r.Message);
        Assert.Contains(m_Calls, c => c == "connect 10.35.78.1:5555");
        Assert.Contains(m_Calls, c => c.Contains("am start -n " + HeadsetLauncher.Package + "/" + HeadsetLauncher.Activity));
    }

    [Fact]
    public void An_app_that_is_already_running_is_not_started_again()
    {
        var l = Make((a, m) => a.StartsWith("connect ") ? (0, "already connected to x") : a.Contains("pidof") ? (0, "6358") : (1, "no"));
        var r = l.Launch();
        Assert.True(r.Ok); Assert.Contains("already running", r.Message);
        Assert.DoesNotContain(m_Calls, c => c.Contains("am start"));
    }

    [Fact]
    public void A_start_error_from_the_headset_is_reported_not_hidden()
    {
        var l = Make((a, m) => a.StartsWith("connect ") ? (0, "connected to x") : a.Contains("pidof") ? (0, "") : (0, "Error: Activity class does not exist."));
        var r = l.Launch();
        Assert.False(r.Ok); Assert.Contains("Error: Activity class does not exist", r.Message);
    }

    [Fact]
    public void Install_without_an_apk_says_there_is_nothing_to_install()
    {
        var l = Make((a, m) => (0, "connected to x"));
        var r = l.Install();
        Assert.False(r.Ok); Assert.Contains("XrSpatialScreens.apk", r.Message);
        Assert.DoesNotContain(m_Calls, c => c.Contains("install"));
    }

    [Fact]
    public void Install_installs_with_replace_then_starts_the_app()
    {
        File.WriteAllText(Path.Combine(m_App, "XrSpatialScreens.apk"), "apk");
        var l = Make((a, m) => a.StartsWith("connect ") ? (0, "connected to x") : a.Contains(" install ") ? (0, "Performing Streamed Install\r\nSuccess") : a.Contains("am start") ? (0, "Starting: Intent") : (1, "no"));
        var r = l.Install();
        Assert.True(r.Ok, r.Message); Assert.Contains("Installed", r.Message);
        Assert.Contains(m_Calls, c => c.Contains(" install -r ") && c.Contains("XrSpatialScreens.apk"));
        Assert.True(m_Calls.FindIndex(c => c.Contains(" install ")) < m_Calls.FindIndex(c => c.Contains("am start")));      // started only after it installed
    }

    [Fact]
    public void A_refused_install_reports_the_reason_and_does_not_start_anything()
    {
        File.WriteAllText(Path.Combine(m_App, "XrSpatialScreens.apk"), "apk");
        var l = Make((a, m) => a.StartsWith("connect ") ? (0, "connected to x") : (1, "Performing Streamed Install\r\nadb: failed to install x.apk: Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]"));
        var r = l.Install();
        Assert.False(r.Ok); Assert.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", r.Message);
        Assert.DoesNotContain(m_Calls, c => c.Contains("am start"));
    }

    [Fact]
    public void Install_on_an_unreachable_headset_says_so()
    {
        File.WriteAllText(Path.Combine(m_App, "XrSpatialScreens.apk"), "apk");
        var r = Make((a, m) => (0, "failed to connect")).Install();
        Assert.False(r.Ok); Assert.Contains("Couldn't reach the headset", r.Message);
    }
}

using System;
using Microsoft.Win32;

namespace XrssHostApp
{
    /// <summary>
    /// "Start with Windows" for the current user: one value under <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> named <c>XrSpatialScreensHost</c>, holding
    /// <c>"&lt;path to XrSpatialScreensHost.exe&gt;" --background</c>. This is the standard per-user startup mechanism: no administrator rights, nothing outside the user's own registry
    /// hive, and turning it off deletes exactly that one value. The key path is a parameter so tests can use a scratch key instead of the real one.
    /// </summary>
    sealed class Autostart
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "XrSpatialScreensHost";
        public const string BackgroundArg = "--background";

        readonly string m_Key;
        public Autostart(string keyPath = RunKey) { m_Key = keyPath; }

        public static string CommandFor(string exePath) => "\"" + exePath + "\" " + BackgroundArg;

        public bool IsEnabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(m_Key)) return k?.GetValue(ValueName) is string s && s.Length > 0; }
        }

        /// <summary>The command stored in the startup entry, or null.</summary>
        public string Command { get { using (var k = Registry.CurrentUser.OpenSubKey(m_Key)) return k?.GetValue(ValueName) as string; } }

        public void Enable(string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(m_Key)) k.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
        }

        public void Disable()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(m_Key, true)) k?.DeleteValue(ValueName, false);
        }

        /// <summary>If the entry exists but points at a different exe path (the app was moved), rewrite it so the next sign-in starts the right file.</summary>
        public void Refresh(string exePath)
        {
            string c = Command;
            if (c != null && c != CommandFor(exePath)) Enable(exePath);
        }
    }
}

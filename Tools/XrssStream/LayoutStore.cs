using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// The PC's copy of the headset's saved screen layouts. The headset keeps its layouts inside the app, which Lepton wipes when it is reset or the app is reinstalled; every layout
/// the headset saves is also sent here, and a freshly installed app asks for them back. Stored as plain files under %LOCALAPPDATA%\XrSpatialScreens\layout-backup.
/// File names are checked (letters, digits, '.', '_', '-' only) so a message from the network can never write outside that folder.
/// </summary>
static class LayoutStore
{
    static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9._-]{1,120}$", RegexOptions.Compiled);
    public const int MaxBytes = 2_000_000;

    public static string Dir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "layout-backup");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static bool IsSafe(string name) => name != null && SafeName.IsMatch(name) && !name.StartsWith(".") && !name.Contains("..");

    public static bool Save(string name, byte[] data)
    {
        if (!IsSafe(name) || data == null || data.Length > MaxBytes) return false;
        string path = Path.Combine(Dir, name), tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);                            // write beside, then swap, so a crash never leaves a half-written backup
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return true;
    }

    public static List<KeyValuePair<string, byte[]>> All()
    {
        var list = new List<KeyValuePair<string, byte[]>>();
        foreach (var f in Directory.GetFiles(Dir))
        {
            string name = Path.GetFileName(f);
            if (!IsSafe(name) || name.EndsWith(".tmp")) continue;
            try { var data = File.ReadAllBytes(f); if (data.Length <= MaxBytes) list.Add(new KeyValuePair<string, byte[]>(name, data)); } catch { }
        }
        return list;
    }
}

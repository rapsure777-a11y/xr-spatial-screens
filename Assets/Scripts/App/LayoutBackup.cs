using System.IO;
using System.Text;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// Keeps a copy of the saved screen layouts on the PC, because Lepton wipes the app (and its saved layouts) when it is reset or the app is reinstalled.
    /// Every saved layout is sent to the PC; each time the connection comes up the layouts already on the headset are sent too, and the PC's copies are requested: any
    /// layout that is missing on the headset is written back, and if the app started with no screens the restored layout is loaded. The headset's own copy always wins.
    /// </summary>
    public sealed class LayoutBackup : MonoBehaviour
    {
        public SpatialWorkspace Workspace;
        const string LastKeyFile = "_lastkey.txt";
        const string Suffix = ".layout.json";

        int m_Epoch = -1;
        float m_NextKeyCheck;
        string m_LastKeySent = "";
        bool m_Restored;

        void Start() { if (Workspace) Workspace.LayoutSaved += OnSaved; }
        void OnDestroy() { if (Workspace) Workspace.LayoutSaved -= OnSaved; }

        void OnSaved(string appKey, string json)
        {
            if (!RemoteHost.Connected) return;
            RemoteHost.SendLayout(Layout.SafeKey(appKey) + Suffix, Encoding.UTF8.GetBytes(json));
        }

        void Update()
        {
            if (!Workspace || !RemoteHost.Active) return;

            if (RemoteHost.Connected && m_Epoch != RemoteHost.ConnectionEpoch)
            {
                m_Epoch = RemoteHost.ConnectionEpoch;
                m_LastKeySent = "";
                PushLocalLayouts();                                                   // the PC learns what the headset has (RemoteHost already asked for the PC's copies)
            }

            if (RemoteHost.Connected && Time.unscaledTime > m_NextKeyCheck)
            {
                m_NextKeyCheck = Time.unscaledTime + 2f;
                string key = PlayerPrefs.GetString("xrss.lastkey", "");
                if (key.Length > 0 && key != m_LastKeySent) { RemoteHost.SendLayout(LastKeyFile, Encoding.UTF8.GetBytes(key)); m_LastKeySent = key; }
            }

            bool any = false;
            while (RemoteHost.TryTakeLayout(out string name, out byte[] data)) any |= Restore(name, data);
            if (any) m_Restored = true;
            // The app started with nothing (a fresh install): show the restored layout. Never replace screens the user already has.
            if (m_Restored && Workspace.Layout.surfaces.Count == 0 && Workspace.Panels.Count == 0)
            {
                m_Restored = false;
                Workspace.LoadLayout(PlayerPrefs.GetString("xrss.lastkey", "default"));
                Debug.Log("[XrSpatial] layouts restored from the PC backup");
            }
        }

        void PushLocalLayouts()
        {
            try
            {
                if (!Directory.Exists(Workspace.LayoutDirectory)) return;
                foreach (var f in Directory.GetFiles(Workspace.LayoutDirectory, "*" + Suffix))
                    RemoteHost.SendLayout(Path.GetFileName(f), File.ReadAllBytes(f));
            }
            catch (System.Exception e) { Debug.LogWarning("[XrSpatial] layout backup upload failed: " + e.Message); }
        }

        /// <summary>Writes a layout that arrived from the PC if the headset does not have it. True when something was restored.</summary>
        bool Restore(string name, byte[] data)
        {
            try
            {
                if (name == LastKeyFile)
                {
                    if (!PlayerPrefs.HasKey("xrss.lastkey")) { PlayerPrefs.SetString("xrss.lastkey", Encoding.UTF8.GetString(data)); return true; }
                    return false;
                }
                if (!name.EndsWith(Suffix) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return false;      // only plain layout file names
                string path = Path.Combine(Workspace.LayoutDirectory, name);
                if (File.Exists(path)) return false;                                  // the headset's own copy wins
                Directory.CreateDirectory(Workspace.LayoutDirectory);
                File.WriteAllBytes(path, data);
                Debug.Log("[XrSpatial] restored layout from the PC: " + name);
                return true;
            }
            catch (System.Exception e) { Debug.LogWarning("[XrSpatial] layout restore failed: " + e.Message); return false; }
        }
    }
}

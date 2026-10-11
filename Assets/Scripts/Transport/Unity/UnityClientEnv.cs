using System;
using System.IO;
using UnityEngine;

namespace XrssTransport.Client
{
    /// <summary>
    /// The Unity/Android implementation of <see cref="IClientEnv"/>. Storage: on the Frame the paired-host credentials and the device identity live in the app's INTERNAL files directory
    /// (<c>Context.getFilesDir()</c>, /data/user/0/&lt;package&gt;/files): app-private, not readable by other apps, and not reachable with adb on a non-debuggable build. This is app-private storage, NOT the
    /// Android Keystore (the secret is a plain file there); see Docs/HEADSET_PAIRING_REPORT.md. Non-secret settings (connection mode, typed address) live in the usual persistent data folder
    /// so a developer can set them with adb. Android requires a Wi-Fi multicast lock to receive mDNS; it is held only during a search.
    /// </summary>
    public sealed class UnityClientEnv : IClientEnv
    {
        public string SecretDir { get; }
        public string SettingsDir { get; }

        /// <summary>Must be created on the Unity main thread (the Application path properties are main-thread only).</summary>
        public UnityClientEnv()
        {
            string baseDir = Application.persistentDataPath;
            string internalDir = baseDir;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                    internalDir = files.Call<string>("getAbsolutePath");
            }
            catch (Exception e) { Debug.LogWarning("[XrSpatial] link: internal files dir unavailable, using the app data folder: " + e.Message); }
#endif
            SecretDir = Path.Combine(internalDir, "xrss-pairing");
            SettingsDir = baseDir;
        }

        public void Log(string message) { Debug.Log("[XrSpatial] " + message); }

        public IDisposable AcquireMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidMulticastLock();
#else
            return new NullMulticastLock();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        sealed class AndroidMulticastLock : IDisposable
        {
            AndroidJavaObject m_Lock;
            public AndroidMulticastLock()
            {
                try
                {
                    AndroidJNI.AttachCurrentThread();                                           // called from a worker thread
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
                    {
                        m_Lock = wifi.Call<AndroidJavaObject>("createMulticastLock", "xrss-discovery");
                        m_Lock.Call("setReferenceCounted", false);
                        m_Lock.Call("acquire");
                    }
                }
                catch (Exception e) { Debug.LogWarning("[XrSpatial] link: multicast lock unavailable: " + e.Message); m_Lock = null; }
            }

            public void Dispose()
            {
                try { if (m_Lock != null) { m_Lock.Call("release"); m_Lock.Dispose(); m_Lock = null; } } catch (Exception) { }
            }
        }
#endif
    }

    /// <summary>The app's single <see cref="PcLink"/>. <see cref="Init"/> must run once on the main thread (RemoteHost.Enable does it); afterwards any thread may use <see cref="Instance"/>.</summary>
    public static class PcLinkHost
    {
        static PcLink s_Instance;
        static readonly object s_Lock = new object();

        public static PcLink Instance { get { lock (s_Lock) return s_Instance; } }

        public static PcLink Init()
        {
            lock (s_Lock)
            {
                if (s_Instance == null)
                {
                    s_Instance = new PcLink(new UnityClientEnv(), null, LinkMode.Dev);       // developer builds keep the plain adb connection until the user switches to Paired PC
                    VectorSelfTest.Run(out string detail);
                    UnityEngine.Debug.Log("[XrSpatial] link: self-test on this runtime: " + detail + "; mode " + s_Instance.Mode + ", paired PCs " + s_Instance.Store.Count);
                }
                return s_Instance;
            }
        }

        /// <summary>Tests only: replace the link.</summary>
        public static void SetForTest(PcLink link) { lock (s_Lock) s_Instance = link; }
    }
}

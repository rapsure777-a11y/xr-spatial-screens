using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.NativeTypes;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Gate1
{
    /// <summary>
    /// Gate 1: asks the OpenXR runtime for XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND and writes everything the runtime offers to the log.
    /// Uses only Unity's public OpenXRFeature.SetEnvironmentBlendMode; no camera code.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "Gate1 Passthrough (alpha blend)", BuildTargetGroups = new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone },
        Company = "Gamebreak Labs", Desc = "Requests XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND and logs the runtime blend modes.", Version = "0.1", FeatureId = FeatureIdString)]
#endif
    public sealed class PassthroughFeature : OpenXRFeature
    {
        public const string FeatureIdString = "com.gamebreak.gate1.passthrough";
        public static string Report = "no report yet";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetProcAddrFn(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumBlendFn(ulong instance, ulong system, int viewConfig, uint capacity, out uint count, int[] modes);

        IntPtr m_Gipa;
        ulong m_Instance;
        readonly StringBuilder m_Sb = new StringBuilder();

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func) { m_Gipa = func; return func; }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            m_Instance = xrInstance;
            Log($"runtime: {OpenXRRuntime.name} {OpenXRRuntime.version} (plugin {OpenXRRuntime.pluginVersion})");
            Log("enabled extensions: " + string.Join(", ", OpenXRRuntime.GetEnabledExtensions()));
            Log("available extensions: " + string.Join(", ", OpenXRRuntime.GetAvailableExtensions()));
            return true;
        }

        protected override void OnSystemChange(ulong xrSystem)
        {
            try
            {
                var gipa = Marshal.GetDelegateForFunctionPointer<GetProcAddrFn>(m_Gipa);
                gipa(m_Instance, "xrEnumerateEnvironmentBlendModes", out var p);
                var enumerate = Marshal.GetDelegateForFunctionPointer<EnumBlendFn>(p);
                const int PrimaryStereo = 2;
                enumerate(m_Instance, xrSystem, PrimaryStereo, 0, out uint n, null);
                var modes = new int[n];
                enumerate(m_Instance, xrSystem, PrimaryStereo, n, out n, modes);
                Log("runtime blend modes (1=opaque 2=additive 3=alpha_blend): " + string.Join(", ", modes));
            }
            catch (Exception e) { Log("blend mode enumeration failed: " + e.Message); }
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            Log("blend mode before request: " + GetEnvironmentBlendMode());
            SetEnvironmentBlendMode(XrEnvironmentBlendMode.AlphaBlend);
            Log("blend mode after request: " + GetEnvironmentBlendMode());
        }

        protected override void OnEnvironmentBlendModeChange(XrEnvironmentBlendMode mode) => Log("blend mode changed: " + mode);

        void Log(string s)
        {
            Debug.Log("[Gate1] " + s);
            m_Sb.AppendLine(s);
            Report = m_Sb.ToString();
        }
    }
}

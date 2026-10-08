using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using XrSpatial.XR;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Editor
{
    /// <summary>
    /// Headset build: the same application as the PC build, as a native Android/OpenXR app for the Steam Frame (ARM64, IL2CPP, Vulkan). It asks the Frame's runtime for the
    /// alpha-blend environment mode (<see cref="PassthroughFeature"/>) so the room shows around the screens, and gets its windows from the PC host over the network.
    /// Batch mode: -executeMethod XrSpatial.Editor.HeadsetBuild.Setup  then (separate launch)  XrSpatial.Editor.HeadsetBuild.Build
    /// </summary>
    public static class HeadsetBuild
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        public const string ApkPath = "Builds/Headset/XrSpatialScreens.apk";

        // Android tools unpacked from the Unity Hub download cache into a user folder (no admin rights); override with the XRSS_ANDROID_TOOLS environment variable.
        static string ToolsRoot => System.Environment.GetEnvironmentVariable("XRSS_ANDROID_TOOLS") ?? @"C:\Users\fence\UnityAndroid";

        [MenuItem("XR Spatial/Configure Headset Build")]
        public static void Setup()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.gamebreaklabs.xrspatialscreens");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel30;
            PlayerSettings.Android.forceInternetPermission = true;                    // TCP to the PC host
            PlayerSettings.SetApiCompatibilityLevel(android, ApiCompatibilityLevel.NET_Standard);

            // The Android quality level (Mobile) renders the same screens as the PC one: no HDR (keeps alpha), no extra textures, MSAA for crisp panel edges.
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (!asset || !asset.name.StartsWith("Mobile")) continue;
                asset.renderScale = 1f;                                              // the stock mobile asset renders at 0.8, which blurs text on a virtual monitor
                asset.msaaSampleCount = 4; asset.supportsHDR = false; asset.supportsCameraDepthTexture = false; asset.supportsCameraOpaqueTexture = false;
                EditorUtility.SetDirty(asset);
            }

            ConfigureXR(BuildTargetGroup.Android);
            AssetDatabase.SaveAssets();
            Debug.Log("[XrSpatial] headset build configured");
        }

        static void ConfigureXR(BuildTargetGroup group)
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
            if (!perTarget)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }
            var settings = perTarget.SettingsForBuildTarget(group);
            if (!settings)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = "Android Settings";
                perTarget.SetSettingsForBuildTarget(group, settings);
                AssetDatabase.AddObjectToAsset(settings, perTarget);
            }
            if (!settings.Manager)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "Android Providers";
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                settings.Manager = manager;
            }
            settings.InitManagerOnStart = true;
            EditorUtility.SetDirty(settings); EditorUtility.SetDirty(perTarget);
            Debug.Log("[XrSpatial] OpenXR loader assigned for Android: " + XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group));
            UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(group);
            var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (!oxr) { Debug.LogError("[XrSpatial] OpenXR settings not found for Android."); return; }
            oxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            oxr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.Depth24Bit;
            foreach (var f in oxr.GetFeatures<PassthroughFeature>()) { f.enabled = true; Debug.Log("[XrSpatial] feature enabled: " + f.name); }
            // Standard controller profiles: whichever one the Frame's runtime picks delivers poses and buttons (it presents the Steam controllers as Touch controllers).
            foreach (var f in oxr.GetFeatures<OpenXRInteractionFeature>())
            {
                string n = f.GetType().Name;
                f.enabled = n == nameof(OculusTouchControllerProfile) || n == nameof(MetaQuestTouchPlusControllerProfile) || n == nameof(MetaQuestTouchProControllerProfile) || n == nameof(KHRSimpleControllerProfile)
                            || n == nameof(ValveIndexControllerProfile) || n == nameof(HTCViveControllerProfile);
            }
            EditorUtility.SetDirty(oxr);
        }

        public static void Build()
        {
            Directory.CreateDirectory("Builds/Headset");
            UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = Path.Combine(ToolsRoot, "SDK");
            UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = Path.Combine(ToolsRoot, "NDK", "android-ndk-r27c");
            UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = Path.Combine(ToolsRoot, "OpenJDK");
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = ApkPath, target = BuildTarget.Android, options = BuildOptions.None });
            Debug.Log($"[XrSpatial] headset build result: {report.summary.result}, size {report.summary.totalSize}, errors {report.summary.totalErrors}");
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}

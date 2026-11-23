using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace Mini.Mobile.Editor
{
    /// <summary>
    /// Quest and phone AR builds share this project and the Android build target, so the Android
    /// settings that differ are switched here in one place:
    /// XR loader (OpenXR for Quest, ARCore for phones), the Quest-only AndroidManifest, graphics API,
    /// orientation, min SDK, package name and the scenes in the build.
    /// </summary>
    public static class MiniPlatformSwitcher
    {
        public const string QuestScene = "Assets/_Project/Scenes/Mini_MR_Robot.unity";
        public const string PhoneScene = "Assets/_Project/Scenes/Mini_Phone_AR.unity";
        public const string BootScene = "Assets/_Project/Scenes/Mini_Phone_Boot.unity";

        private const string ActiveManifest = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string QuestManifestBackup = "Assets/_Project/Platform/Quest/QuestAndroidManifest.xml.txt";

        private const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string ARCoreLoader = "UnityEngine.XR.ARCore.ARCoreLoader";
        private const string ARKitLoader = "UnityEngine.XR.ARKit.ARKitLoader";

        private const string QuestPackage = "com.minicompanion.mini";
        private const string PhonePackage = "com.minicompanion.mini.phone";

        private const string PhoneMenu = "Mini/Platform/Phone AR (Android + iOS)";
        private const string QuestMenu = "Mini/Platform/Meta Quest";

        public static bool IsPhone
        {
            get { return PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) == PhonePackage; }
        }

        [MenuItem(PhoneMenu, false, 10)]
        public static void SwitchToPhone()
        {
            if (!File.Exists(PhoneScene) || !File.Exists(BootScene))
            {
                if (!EditorUtility.DisplayDialog("Mini phone AR",
                        "The phone scene doesn't exist yet. Build it now from the Quest scene?", "Build it", "Cancel"))
                    return;
                MobileSceneBuilder.BuildPhoneScene();
            }

            // Quest manifest declares VR head tracking and passthrough as required: park it.
            if (File.Exists(ActiveManifest))
            {
                EnsureFolder(Path.GetDirectoryName(QuestManifestBackup));
                if (File.Exists(QuestManifestBackup)) AssetDatabase.DeleteAsset(QuestManifestBackup);
                string error = AssetDatabase.MoveAsset(ActiveManifest, QuestManifestBackup);
                if (!string.IsNullOrEmpty(error)) { Debug.LogError("[Mini] Could not move the Quest manifest: " + error); return; }
            }

            SetLoaders(BuildTargetGroup.Android, ARCoreLoader, OpenXRLoader);
            SetLoaders(BuildTargetGroup.iOS, ARKitLoader, null);

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PhonePackage);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, PhonePackage);
            PlayerSettings.iOS.cameraUsageDescription = "Mini uses the camera to stand in your room.";
            PlayerSettings.iOS.microphoneUsageDescription = "Mini listens so you can talk to it.";
            if (PlayerSettings.iOS.targetOSVersionString == null ||
                string.CompareOrdinal(PlayerSettings.iOS.targetOSVersionString, "15.0") < 0)
                PlayerSettings.iOS.targetOSVersionString = "15.0";

            SetBuildScenes(BootScene, PhoneScene);
            Finish("Phone AR");
        }

        [MenuItem(QuestMenu, false, 11)]
        public static void SwitchToQuest()
        {
            if (File.Exists(QuestManifestBackup) && !File.Exists(ActiveManifest))
            {
                EnsureFolder(Path.GetDirectoryName(ActiveManifest));
                string error = AssetDatabase.MoveAsset(QuestManifestBackup, ActiveManifest);
                if (!string.IsNullOrEmpty(error)) { Debug.LogError("[Mini] Could not restore the Quest manifest: " + error); return; }
            }

            SetLoaders(BuildTargetGroup.Android, OpenXRLoader, ARCoreLoader);

            // Quest project default: Vulkan first, GLES3 fallback.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, QuestPackage);

            SetBuildScenes(QuestScene);
            Finish("Meta Quest");
        }

        [MenuItem(PhoneMenu, true)]
        private static bool ValidatePhone() { Menu.SetChecked(PhoneMenu, IsPhone); return true; }

        [MenuItem(QuestMenu, true)]
        private static bool ValidateQuest() { Menu.SetChecked(QuestMenu, !IsPhone); return true; }

        private static void Finish(string platform)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            Debug.Log("[Mini] Switched project to " + platform + ". Build with File > Build Profiles > Android.");
        }

        /// <summary>Enable exactly these scenes, in this order (the first one loads at startup).</summary>
        private static void SetBuildScenes(params string[] enabledScenes)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            foreach (string path in enabledScenes)
                if (!scenes.Any(s => s.path == path)) scenes.Add(new EditorBuildSettingsScene(path, true));
            foreach (EditorBuildSettingsScene s in scenes) s.enabled = enabledScenes.Contains(s.path);
            scenes = scenes.OrderBy(s => s.enabled ? System.Array.IndexOf(enabledScenes, s.path) : enabledScenes.Length).ToList();
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void SetLoaders(BuildTargetGroup group, string add, string remove)
        {
            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            if (settings == null) settings = TryCreateSettings(group);
            if (settings != null && settings.Manager == null)
            {
                // Freshly created platform entries have no loader manager yet: add one beside the settings.
                XRManagerSettings manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = group + " Providers";
                AssetDatabase.AddObjectToAsset(manager, AssetDatabase.GetAssetPath(settings));
                settings.Manager = manager;
                EditorUtility.SetDirty(settings);
            }
            if (settings == null || settings.Manager == null)
            {
                Debug.LogWarning("[Mini] No XR settings for " + group + ". Open Project Settings > XR Plug-in Management, " +
                                 "select the " + group + " tab once, then run this again.");
                return;
            }

            if (!string.IsNullOrEmpty(remove)) XRPackageMetadataStore.RemoveLoader(settings.Manager, remove, group);
            if (!XRPackageMetadataStore.AssignLoader(settings.Manager, add, group))
                Debug.LogWarning("[Mini] Could not enable " + add + " for " + group + ". Is its package installed?");
            settings.InitManagerOnStart = true;
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(settings.Manager);
        }

        /// <summary>XR Management only creates per-platform settings from its settings UI; try its internal helper.</summary>
        private static XRGeneralSettings TryCreateSettings(BuildTargetGroup group)
        {
            XRGeneralSettingsPerBuildTarget all;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out all) || all == null) return null;
            MethodInfo create = typeof(XRGeneralSettingsPerBuildTarget).GetMethod("CreateDefaultSettingsForBuildTarget",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (create == null) return null;
            try { create.Invoke(all, new object[] { group }); }
            catch (Exception e) { Debug.LogWarning("[Mini] " + e.Message); return null; }
            return XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        }

        private static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        internal static void EnsureAssetFolder(string folder) { EnsureFolder(folder); }
    }
}

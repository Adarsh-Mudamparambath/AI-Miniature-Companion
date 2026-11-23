using System;
using System.Collections.Generic;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using Mini.App;
using Mini.Controls;
using Mini.MR;
using Mini.Voice;

namespace Mini.Mobile.Editor
{
    /// <summary>
    /// Builds Mini_Phone_AR.unity from the Quest scene: same Mini, services and HUD, but the Meta
    /// camera rig, passthrough and Touch controller input are replaced by an AR Foundation rig
    /// (ARCore / ARKit), plane detection and on-screen touch controls. Safe to re-run: it rebuilds
    /// the phone scene from the current Quest scene each time.
    /// </summary>
    public static class MobileSceneBuilder
    {
        private const string PlanePrefabPath = "Assets/_Project/Prefabs/Mobile/ARPlaneVisual.prefab";
        private const string PlaneMaterialPath = "Assets/_Project/Prefabs/Mobile/ARPlaneVisual.mat";

        [MenuItem("Mini/Phone AR/Rebuild Phone AR Scene", false, 30)]
        public static void BuildPhoneScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MiniPlatformSwitcher.PhoneScene) != null)
                AssetDatabase.DeleteAsset(MiniPlatformSwitcher.PhoneScene);
            if (!AssetDatabase.CopyAsset(MiniPlatformSwitcher.QuestScene, MiniPlatformSwitcher.PhoneScene))
            {
                Debug.LogError("[Mini] Could not copy " + MiniPlatformSwitcher.QuestScene);
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(MiniPlatformSwitcher.PhoneScene, OpenSceneMode.Single);
            List<string> log = new List<string>();

            RemoveQuestRig(scene, log);
            GameObject planePrefab = EnsurePlanePrefab();
            Camera arCamera;
            ARRaycastManager raycaster;
            ARSession session;
            CreateARRig(planePrefab, out arCamera, out raycaster, out session, log);
            EnsureEventSystem(log);
            WireMini(scene, arCamera, raycaster, session, log);
            AddARBackgroundToRenderers(log);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            BuildBootScene(log);
            EditorSceneManager.OpenScene(MiniPlatformSwitcher.PhoneScene, OpenSceneMode.Single);
            Debug.Log("[Mini] Built " + MiniPlatformSwitcher.PhoneScene + ":\n- " + string.Join("\n- ", log));
        }

        // ------------------------------------------------------------ Quest rig removal

        private static bool IsMetaType(Type t)
        {
            string ns = t.Namespace ?? string.Empty;
            return t.Name.StartsWith("OVR", StringComparison.Ordinal) || ns.StartsWith("Oculus", StringComparison.Ordinal) ||
                   ns.StartsWith("Meta.", StringComparison.Ordinal);
        }

        private static IEnumerable<GameObject> AllObjects(Scene scene)
        {
            return scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject);
        }

        private static void RemoveQuestRig(Scene scene, List<string> log)
        {
            // The camera rig (OVRCameraRig) goes entirely; keep any Mini objects that were parented under it.
            foreach (Component rig in AllObjects(scene).SelectMany(g => g.GetComponents<Component>())
                         .Where(c => c != null && c.GetType().Name == "OVRCameraRig").ToList())
            {
                foreach (MonoBehaviour mini in rig.GetComponentsInChildren<MonoBehaviour>(true)
                             .Where(m => m != null && (m.GetType().Namespace ?? "").StartsWith("Mini", StringComparison.Ordinal)).ToList())
                {
                    if (mini.transform.IsChildOf(rig.transform) && mini.transform != rig.transform)
                        mini.transform.SetParent(null, true);
                }
                log.Add("Removed Quest camera rig '" + rig.gameObject.name + "'");
                UnityEngine.Object.DestroyImmediate(rig.gameObject);
            }

            // Quest-only Mini components.
            Type[] questOnly = { typeof(MiniControllerInput), typeof(EditorNoXRFallback), typeof(PassthroughStatusLogger) };
            foreach (GameObject go in AllObjects(scene).ToList())
            {
                if (go == null) continue;
                foreach (Type t in questOnly)
                {
                    Component c = go.GetComponent(t);
                    if (c == null) continue;
                    bool onlyThis = go.GetComponents<Component>().Length == 2 && go.transform.childCount == 0;
                    log.Add("Removed " + t.Name + (onlyThis ? " (and its object '" + go.name + "')" : " from '" + go.name + "'"));
                    if (onlyThis) { UnityEngine.Object.DestroyImmediate(go); break; }
                    UnityEngine.Object.DestroyImmediate(c);
                }
            }

            // Any other Meta components (OVRManager, passthrough layers...). Retry for RequireComponent chains.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Component c in AllObjects(scene).Where(g => g != null).SelectMany(g => g.GetComponents<Component>())
                             .Where(c => c != null && IsMetaType(c.GetType())).ToList())
                {
                    string desc = c.GetType().Name + " from '" + c.gameObject.name + "'";
                    GameObject owner = c.gameObject;
                    UnityEngine.Object.DestroyImmediate(c);
                    if (c == null) log.Add("Removed " + desc);
                    if (owner != null && owner.GetComponents<Component>().Length == 1 && owner.transform.childCount == 0)
                        UnityEngine.Object.DestroyImmediate(owner);
                }
            }

            // Old cameras that survived (e.g. an editor fallback camera).
            foreach (Camera cam in AllObjects(scene).Where(g => g != null).Select(g => g.GetComponent<Camera>()).Where(c => c != null).ToList())
            {
                log.Add("Removed old camera '" + cam.gameObject.name + "'");
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }

            foreach (GameObject go in AllObjects(scene).Where(g => g != null).ToList())
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
        }

        // ------------------------------------------------------------ AR rig

        private static void CreateARRig(GameObject planePrefab, out Camera cam, out ARRaycastManager raycaster,
            out ARSession session, List<string> log)
        {
            Selection.activeObject = null;
            session = UnityEngine.Object.FindAnyObjectByType<ARSession>();
            if (session == null && EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session"))
                session = UnityEngine.Object.FindAnyObjectByType<ARSession>();
            if (session == null)
            {
                GameObject s = new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));
                session = s.GetComponent<ARSession>();
            }
            log.Add("Added AR Session");

            Selection.activeObject = null;
            XROrigin origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (origin == null && EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)"))
                origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (origin == null) throw new InvalidOperationException(
                "Could not create 'XR Origin (Mobile AR)'. Is the AR Foundation package installed?");
            origin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            log.Add("Added XR Origin (Mobile AR)");

            cam = origin.Camera;
            cam.tag = "MainCamera";
            cam.nearClipPlane = 0.05f;
            if (cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();

            ARPlaneManager planes = origin.GetComponent<ARPlaneManager>();
            if (planes == null) planes = origin.gameObject.AddComponent<ARPlaneManager>();
            planes.requestedDetectionMode = UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Horizontal;
            planes.planePrefab = planePrefab;

            raycaster = origin.GetComponent<ARRaycastManager>();
            if (raycaster == null) raycaster = origin.gameObject.AddComponent<ARRaycastManager>();
            log.Add("Plane detection (horizontal) + raycasts on the XR Origin");
        }

        private static GameObject EnsurePlanePrefab()
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlanePrefabPath);
            if (existing != null) return existing;
            MiniPlatformSwitcher.EnsureAssetFolder("Assets/_Project/Prefabs/Mobile");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(PlaneMaterialPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                mat = new Material(shader);
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.12f));
                AssetDatabase.CreateAsset(mat, PlaneMaterialPath);
            }

            GameObject go = new GameObject("ARPlaneVisual", typeof(ARPlane), typeof(ARPlaneMeshVisualizer),
                typeof(MeshFilter), typeof(MeshRenderer), typeof(LineRenderer));
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            LineRenderer line = go.GetComponent<LineRenderer>();
            line.sharedMaterial = mat;
            line.widthMultiplier = 0.005f;
            line.useWorldSpace = false;
            line.loop = true;
            line.startColor = line.endColor = new Color(1f, 1f, 1f, 0.6f);
            line.shadowCastingMode = ShadowCastingMode.Off;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, PlanePrefabPath);
            UnityEngine.Object.DestroyImmediate(go);
            return prefab;
        }

        private static void EnsureEventSystem(List<string> log)
        {
            EventSystem es = UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            if (es == null)
            {
                es = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
                log.Add("Added EventSystem");
            }
            foreach (BaseInputModule m in es.GetComponents<BaseInputModule>())
                if (!(m is InputSystemUIInputModule)) UnityEngine.Object.DestroyImmediate(m);
            if (es.GetComponent<InputSystemUIInputModule>() == null) es.gameObject.AddComponent<InputSystemUIInputModule>();
        }

        // ------------------------------------------------------------ Mini wiring

        private static void WireMini(Scene scene, Camera cam, ARRaycastManager raycaster, ARSession session, List<string> log)
        {
            MiniCompanionApp app = UnityEngine.Object.FindAnyObjectByType<MiniCompanionApp>(FindObjectsInactive.Include);
            PlacementManager placement = UnityEngine.Object.FindAnyObjectByType<PlacementManager>(FindObjectsInactive.Include);
            CompanionHud hud = UnityEngine.Object.FindAnyObjectByType<CompanionHud>(FindObjectsInactive.Include);
            VoiceManager voice = UnityEngine.Object.FindAnyObjectByType<VoiceManager>(FindObjectsInactive.Include);
            if (app == null || placement == null)
                throw new InvalidOperationException("The Quest scene has no MiniCompanionApp / PlacementManager to copy.");

            MobileTouchControls controls = app.GetComponent<MobileTouchControls>();
            if (controls == null) controls = app.gameObject.AddComponent<MobileTouchControls>();
            Set(controls, "placement", placement);
            Set(controls, "voice", voice);
            Set(controls, "arCamera", cam);

            ManualSurfaceProvider manual = placement.GetComponent<ManualSurfaceProvider>();
            if (manual != null) UnityEngine.Object.DestroyImmediate(manual);
            ARPlaneSurfaceProvider surfaces = placement.GetComponent<ARPlaneSurfaceProvider>();
            if (surfaces == null) surfaces = placement.gameObject.AddComponent<ARPlaneSurfaceProvider>();
            Set(surfaces, "raycastManager", raycaster);

            Set(placement, "userHead", cam.transform);
            Set(placement, "pointer", cam.transform);
            Set(placement, "environmentProvider", surfaces);
            Set(placement, "controls", controls);
            SetBool(placement, "hideUntilPlaced", true);

            Set(app, "userHead", cam.transform);
            Set(app, "controls", controls);
            MiniSettings phoneSettings = EnsurePhoneSettings(app, log);
            if (phoneSettings != null) Set(app, "settings", phoneSettings);

            if (hud != null)
            {
                SetBool(hud, "screenSpacePanel", true);
                SetFloat(hud, "labelScale", 0.45f);
            }

            // Main menu + session flow. The AR session (camera) stays off until Start is pressed.
            session.enabled = false;
            MobileAppFlow flow = app.GetComponent<MobileAppFlow>();
            if (flow == null) flow = app.gameObject.AddComponent<MobileAppFlow>();
            Set(flow, "app", app);
            Set(flow, "session", session);
            Set(flow, "controls", controls);
            Set(flow, "hud", hud);
            log.Add("Added main menu flow (AR camera off until Start; Leave / Exit)");
            Mini.Character.CharacterManager character =
                UnityEngine.Object.FindAnyObjectByType<Mini.Character.CharacterManager>(FindObjectsInactive.Include);
            if (character != null) Set(character, "userHead", cam.transform);
            log.Add("Wired touch controls, AR plane surfaces, AR camera and screen-space guidance into Mini");

            // Anything else that pointed at the removed Quest rig is now empty: list it for review.
            foreach (MonoBehaviour mb in AllObjects(scene).Where(g => g != null).SelectMany(g => g.GetComponents<MonoBehaviour>())
                         .Where(m => m != null && (m.GetType().Namespace ?? "").StartsWith("Mini", StringComparison.Ordinal)))
            {
                SerializedObject so = new SerializedObject(mb);
                SerializedProperty p = so.GetIterator();
                while (p.NextVisible(true))
                {
                    if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null &&
                        p.objectReferenceInstanceIDValue != 0)
                        log.Add("Check: " + mb.GetType().Name + "." + p.propertyPath + " lost its reference");
                }
            }
        }

        private const string PhoneSettingsPath = "Assets/_Project/Settings/MiniSettings_Phone.asset";

        /// <summary>
        /// Phone copy of the Quest settings, made once and then kept (so phone tweaks survive rebuilds).
        /// Barge-in is off: a phone's loudspeaker sits next to its microphone, so Mini would hear itself
        /// and cut its own replies short.
        /// </summary>
        private static MiniSettings EnsurePhoneSettings(MiniCompanionApp app, List<string> log)
        {
            MiniSettings phone = AssetDatabase.LoadAssetAtPath<MiniSettings>(PhoneSettingsPath);
            if (phone != null) { ApplyPhoneRules(phone); return phone; }

            MiniSettings quest = new SerializedObject(app).FindProperty("settings").objectReferenceValue as MiniSettings;
            string questPath = quest != null ? AssetDatabase.GetAssetPath(quest) : null;
            if (string.IsNullOrEmpty(questPath) || !AssetDatabase.CopyAsset(questPath, PhoneSettingsPath))
            {
                phone = ScriptableObject.CreateInstance<MiniSettings>();
                AssetDatabase.CreateAsset(phone, PhoneSettingsPath);
            }
            phone = AssetDatabase.LoadAssetAtPath<MiniSettings>(PhoneSettingsPath);
            ApplyPhoneRules(phone);
            log.Add("Created " + PhoneSettingsPath + " (barge-in off for the phone speaker)");
            return phone;
        }

        /// <summary>Settings the phone flow depends on: quiet until placed, placement starts from the menu.</summary>
        private static void ApplyPhoneRules(MiniSettings phone)
        {
            phone.allowBargeIn = false;
            phone.talkAfterPlacement = true;
            phone.startInPlacementMode = false;
            EditorUtility.SetDirty(phone);
            AssetDatabase.SaveAssets();
        }

        /// <summary>The loading screen scene: a camera with the background colour and the boot loader.</summary>
        private static void BuildBootScene(List<string> log)
        {
            Scene boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject camGo = new GameObject("Camera", typeof(Camera));
            Camera cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Mini.UI.MiniUi.BackgroundBottom;
            cam.cullingMask = 0;
            GameObject loader = new GameObject("MiniBoot", typeof(MobileBoot));
            SerializedObject so = new SerializedObject(loader.GetComponent<MobileBoot>());
            so.FindProperty("mainScene").stringValue = System.IO.Path.GetFileNameWithoutExtension(MiniPlatformSwitcher.PhoneScene);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(boot, MiniPlatformSwitcher.BootScene);
            log.Add("Built loading screen scene " + MiniPlatformSwitcher.BootScene);
        }

        private static void SetFloat(UnityEngine.Object target, string field, float value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning("[Mini] " + target.GetType().Name + " has no field '" + field + "'"); return; }
            p.floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning("[Mini] " + target.GetType().Name + " has no field '" + field + "'"); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBool(UnityEngine.Object target, string field, bool value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning("[Mini] " + target.GetType().Name + " has no field '" + field + "'"); return; }
            p.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------ URP camera background

        /// <summary>
        /// URP needs ARBackgroundRendererFeature to draw the phone camera image. It only acts on cameras
        /// with an ARCameraBackground, so adding it to the shared renderers does not affect the Quest build.
        /// </summary>
        private static void AddARBackgroundToRenderers(List<string> log)
        {
            HashSet<RenderPipelineAsset> assets = new HashSet<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline != null) assets.Add(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                RenderPipelineAsset a = QualitySettings.GetRenderPipelineAssetAt(i);
                if (a != null) assets.Add(a);
            }

            foreach (UniversalRenderPipelineAsset urp in assets.OfType<UniversalRenderPipelineAsset>())
            {
                SerializedProperty list = new SerializedObject(urp).FindProperty("m_RendererDataList");
                if (list == null) continue;
                for (int i = 0; i < list.arraySize; i++)
                {
                    ScriptableRendererData data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                    if (data == null || data.rendererFeatures.Any(f => f is ARBackgroundRendererFeature)) continue;

                    ARBackgroundRendererFeature feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
                    feature.name = "ARBackgroundRendererFeature";
                    AssetDatabase.AddObjectToAsset(feature, data);

                    string guid;
                    long localId;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out localId);
                    SerializedObject so = new SerializedObject(data);
                    SerializedProperty features = so.FindProperty("m_RendererFeatures");
                    SerializedProperty map = so.FindProperty("m_RendererFeatureMap");
                    features.arraySize++;
                    features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(data);
                    log.Add("Added AR camera background to renderer '" + data.name + "'");
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}

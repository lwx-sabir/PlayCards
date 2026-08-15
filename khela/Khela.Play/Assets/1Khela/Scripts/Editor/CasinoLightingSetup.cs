#if UNITY_EDITOR
using System.Collections.Generic;
using PlayCard.Quality;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// One-click "casino interior" lighting rig for the open Table scene. The flat look comes from full ambient
    /// (Environment = Skybox @ intensity 1) lighting every surface equally; this replaces that with a dark room +
    /// a focused warm downlight over the table, so gameplay reads as a lit table in a dim lounge.
    ///
    /// It sets up the LIGHTING model chosen for the scene: bake the static room (dark, with GI + soft shadows) and
    /// light the table with a REALTIME warm spot for the moving cards/dealer/chips, with a light-probe grid so
    /// those dynamic objects sample the local table light instead of the flat baked dark.
    ///
    /// After running: tune <c>Casino_TableSpot</c> to taste, then Window ▸ Rendering ▸ Lighting ▸ Generate Lighting.
    /// Baking the (already-static) room is ALSO the fix for the static-without-bake fps cost. For mobile, bake
    /// Non-Directional at a modest lightmap resolution, and pick the Subtractive mixed-lighting mode.
    ///
    /// Idempotent: re-running updates the same <c>Casino_TableSpot</c> / <c>Casino_TableProbes</c> objects (found by
    /// name) instead of duplicating them, and repositions them over the current selection.
    /// </summary>
    public static class CasinoLightingSetup
    {
        private const string SpotName = "Casino_TableSpot";
        private const string ProbesName = "Casino_TableProbes";

        // --- tunables (starting points; tune in the scene) ---
        private static readonly Color AmbientDark = new Color(0.040f, 0.035f, 0.030f); // near-black, faintly warm
        private static readonly Color SpotWarm     = new Color(1.000f, 0.860f, 0.660f); // ~3300 K lamp
        private static readonly Color DirFill      = new Color(0.800f, 0.820f, 0.900f); // dim cool room fill
        private const float DirIntensity = 0.20f;   // was 1.0 — now just a hint of shape so nothing is pure black
        private const float SpotHeight   = 3.0f;
        private const float SpotIntensity = 4.5f;   // tune against your URP light unit
        private const float SpotRange    = 7.0f;
        private const float SpotAngle    = 64f;
        private const float SpotInner    = 42f;

        [MenuItem("Khela/Lighting/Apply Casino Lighting (Open Scene)")]
        private static void Apply()
        {
            // 1) Kill the flat fill — Environment ambient becomes a dark colour (the single biggest lever).
            RenderSettings.ambientMode = AmbientMode.Flat;   // Flat = "Color" source
            RenderSettings.ambientLight = AmbientDark;

            // 2) Fog OFF — deliberately. RenderSettings fog is applied by every fog-enabled shader in view, particle
            //    shaders included, so a dark fog turns additive UI sparkles BLACK (UI Image/Sprite shaders ignore
            //    fog — that's why the button stays bright but its particles go dark). The baked dark room + the table
            //    spot + the vignette carry the "dim lounge" look without it. Setting it false here also CLEARS any
            //    fog a previous run enabled. Want fog for 3D depth later? Do it so it can't touch UI (unlit no-fog
            //    particle materials, or a scene-depth fog), never via global RenderSettings.
            RenderSettings.fog = false;

            // 3) Directional → a dim fill, Mixed so it bakes into the room.
            var dir = FindDirectional();
            if (dir != null)
            {
                Undo.RecordObject(dir, "Casino directional");
                dir.intensity = DirIntensity;
                dir.color = DirFill;
                dir.lightmapBakeType = LightmapBakeType.Mixed;
                dir.shadows = LightShadows.Soft;
            }
            else
            {
                Debug.LogWarning("[CasinoLighting] No directional light found — skipping the fill light.");
            }

            // Table centre: the selected object if any, else world origin (with a warning).
            Transform anchor = Selection.activeTransform;
            Vector3 center = anchor != null ? anchor.position : Vector3.zero;
            if (anchor == null)
                Debug.LogWarning("[CasinoLighting] Nothing selected — placing the table spot at world origin. " +
                                 "Select the table root and re-run to reposition it.");

            // 4) The focus — a realtime warm downlight over the table (lights the moving cards/dealer/chips).
            var spotGo = CreateOrFind(SpotName, out _);
            var spot = spotGo.GetComponent<Light>();
            if (spot == null) spot = spotGo.AddComponent<Light>();   // Unity-aware check, NOT ?? (which keeps fake-null)
            spotGo.transform.SetPositionAndRotation(center + Vector3.up * SpotHeight, Quaternion.Euler(90f, 0f, 0f)); // down
            spot.type = LightType.Spot;
            spot.color = SpotWarm;
            spot.intensity = SpotIntensity;
            spot.range = SpotRange;
            spot.spotAngle = SpotAngle;
            spot.innerSpotAngle = SpotInner;
            spot.lightmapBakeType = LightmapBakeType.Realtime;   // realtime → lights dynamic cards; room shell is baked
            spot.shadows = LightShadows.Soft;
            spot.shadowStrength = 0.8f;

            // 5) Light-probe grid over the table so dynamic objects (cards, dealer, chips, avatars) pick up the
            //    table light + the surrounding dark, instead of rendering flat.
            var probesGo = CreateOrFind(ProbesName, out _);
            probesGo.transform.SetPositionAndRotation(center, Quaternion.identity);
            var group = probesGo.GetComponent<LightProbeGroup>();
            if (group == null) group = probesGo.AddComponent<LightProbeGroup>();
            group.probePositions = BuildProbeGrid();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = spot.gameObject;

            Debug.Log("[CasinoLighting] Applied. Next: tune Casino_TableSpot (intensity/height/cone/colour), then " +
                      "Window ▸ Rendering ▸ Lighting ▸ Generate Lighting. Mobile: Non-Directional lightmaps, modest " +
                      "resolution, Subtractive mixed mode.");
        }

        private static Light FindDirectional()
        {
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) return l;
            return null;
        }

        private static GameObject CreateOrFind(string name, out bool created)
        {
            var go = GameObject.Find(name);
            created = go == null;
            if (created)
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            }
            return go;
        }

        /// <summary>4×4 horizontal grid at two heights (hand height + head height) spanning ~the table footprint.</summary>
        private static Vector3[] BuildProbeGrid()
        {
            const float half = 1.9f;
            const int n = 4;
            float[] heights = { 0.35f, 1.5f };

            var positions = new List<Vector3>(n * n * heights.Length);
            foreach (float y in heights)
                for (int ix = 0; ix < n; ix++)
                    for (int iz = 0; iz < n; iz++)
                    {
                        float x = Mathf.Lerp(-half, half, ix / (float)(n - 1));
                        float z = Mathf.Lerp(-half, half, iz / (float)(n - 1));
                        positions.Add(new Vector3(x, y, z));
                    }
            return positions.ToArray();
        }

        // ------------------------------------------------------------------ post-processing focus

        private const string PostVolumeName = "Casino_PostVolume";
        private const string FocusProfilePath = "Assets/1Khela/Quality/PostProcess/PP_TableFocus.asset";

        /// <summary>
        /// Adds a TABLE-SCENE-ONLY post volume (strong vignette + warm grade) that layers on top of the
        /// PostFxTierController stack. Because the volume GameObject lives only in this scene, the focus look can't
        /// bleed into Home/Lobby, which share the PP tier profiles. Vignette does the heavy lifting — it darkens the
        /// screen edges so the eye locks onto the lit table.
        /// </summary>
        [MenuItem("Khela/Lighting/Add Table Post Focus (Vignette + Warm Grade)")]
        private static void AddTablePostFocus()
        {
            var profile = LoadOrCreateFocusProfile();

            var go = GameObject.Find(PostVolumeName);
            if (go == null)
            {
                go = new GameObject(PostVolumeName);
                Undo.RegisterCreatedObjectUndo(go, "Create " + PostVolumeName);
            }

            // Must sit on a layer inside the camera's Volume Mask, or it's invisible — match the tier volume's layer.
            var controller = Object.FindFirstObjectByType<PostFxTierController>();
            if (controller != null && controller.BaseVolume != null)
                go.layer = controller.BaseVolume.gameObject.layer;
            else
                Debug.LogWarning("[CasinoLighting] No PostFxTierController found — put Casino_PostVolume on a layer " +
                                 "included in the camera's Volume Mask, or the vignette won't show.");

            var vol = go.GetComponent<Volume>();
            if (vol == null) vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.blendDistance = 0f;
            vol.priority = 30f;   // above the tier override volume (default priority 10)
            vol.weight = 1f;
            vol.sharedProfile = profile;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = go;
            Debug.Log("[CasinoLighting] Table post-focus added (PP_TableFocus) — applies only in this scene. Tune the " +
                      "Vignette intensity + warm grade on that profile to taste.");
        }

        private static VolumeProfile LoadOrCreateFocusProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(FocusProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, FocusProfilePath);
            }

            // Vignette — the focus star.
            var vig = GetOrAdd<Vignette>(profile);
            vig.active = true;
            Set(vig.intensity, 0.42f);
            Set(vig.smoothness, 0.45f);
            Set(vig.color, new Color(0.02f, 0.01f, 0f, 1f));

            // Warm, slightly darker lounge grade.
            var ca = GetOrAdd<ColorAdjustments>(profile);
            ca.active = true;
            Set(ca.postExposure, -0.15f);
            Set(ca.contrast, 6f);
            Set(ca.saturation, -3f);
            Set(ca.colorFilter, new Color(1f, 0.96f, 0.9f, 1f));

            var wb = GetOrAdd<WhiteBalance>(profile);
            wb.active = true;
            Set(wb.temperature, 12f);   // warmer

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        /// <summary>Get a component from the profile, or add it AND persist it as a hidden sub-asset (required — a
        /// component added at runtime isn't saved with the profile asset without AddObjectToAsset).</summary>
        private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing)) return existing;
            var comp = profile.Add<T>();
            comp.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(comp, profile);
            return comp;
        }

        private static void Set(FloatParameter p, float value) { p.overrideState = true; p.value = value; }
        private static void Set(ColorParameter p, Color value) { p.overrideState = true; p.value = value; }
    }
}
#endif

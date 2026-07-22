#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Prep pass that must run BEFORE <see cref="VirtualWorldLightingBaker"/>. The Synty world scenes ship with
    /// nothing marked Static and every light Realtime, which means a lightmap bake has no receivers and no
    /// baked emitters — it completes and produces an empty LightingData with zero Lightmap-*.exr files.
    ///
    /// This tool fixes the three prerequisites, in the order that matters:
    ///   1. Static flags on environment geometry  (unlocks lightmapping, static batching, occlusion culling)
    ///   2. Lights Realtime -> Baked              (so they actually contribute to the lightmap)
    ///   3. A Light Probe grid                    (the ONLY thing that lights dynamic avatars once GI is baked)
    ///   + Reflection probes Realtime -> Baked
    ///
    /// NOTE ON SHADOWS: converting a light to Baked and then disabling its shadows is a classic mistake — for a
    /// Baked light the Shadow Type controls whether the LIGHTMAPPER computes occlusion. Turning it off bakes
    /// direct light with no shadows, so light leaks through walls. Shadow settings are deliberately left alone;
    /// the runtime cost disappears simply by virtue of the light being Baked.
    ///
    /// SEPARATE PREREQUISITE (done outside this tool): the source FBXs need "Generate Lightmap UVs" enabled.
    /// Without a UV2 channel Unity falls back to UV0, which on Synty models is a palette-atlas lookup with
    /// massively overlapping coordinates — the bake would produce garbage. Run the report to verify.
    ///
    /// Everything here is undoable and reports what it touched. Editor-only.
    /// </summary>
    public static class WorldScenePrep
    {
        private const string Root = "Assets/1Khela/_Scenes/Virtual_Worlds";

        // --- light-probe grid tuning ---------------------------------------------------------------
        private const float ProbeSpacing = 3f;              // horizontal grid step, metres
        private static readonly float[] ProbeHeights = { 0.4f, 1.3f, 2.2f };  // ankle / body / head-ish
        private const int   MaxProbes = 3000;               // hard cap so a big room can't explode the count
        private const float ProbeClearance = 0.25f;         // skip probes buried inside geometry

        private const string ProbeGroupName = "Light Probe Group (Generated)";

        // ============================================================================================
        // 1. REPORT — changes nothing. Run this first.
        // ============================================================================================

        [MenuItem("Tools/Khela/World Prep/1 - Report Scene Readiness", priority = 20)]
        public static void ReportSceneReadiness()
        {
            var scene = SceneManager.GetActiveScene();
            var sb = new StringBuilder();
            sb.AppendLine($"=== World Prep report — {scene.name} ===");

            var preppable = new HashSet<GameObject>();
            int skippedDynamic = 0;
            ForEachPreppable(scene, go => preppable.Add(go), () => skippedDynamic++);

            // Static state today
            int alreadyStatic = Object
                .FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(g => g.scene == scene && GameObjectUtility.GetStaticEditorFlags(g) != 0);

            sb.AppendLine($"Static: {alreadyStatic} object(s) currently static; {preppable.Count} would be marked, " +
                          $"{skippedDynamic} subtree(s) skipped as dynamic.");

            // Lightmap UVs — the prerequisite that silently ruins a bake. Only the STATIC set matters: dynamic
            // meshes (the player, held weapons) are lit by probes and never lightmapped, so their missing UV2
            // is expected and harmless — checking them would cry wolf.
            var staticRenderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                        .Where(r => r.gameObject.scene == scene && preppable.Contains(r.gameObject))
                                        .ToArray();
            var missingUv2 = new HashSet<string>();
            foreach (var r in staticRenderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
                {
                    var path = AssetDatabase.GetAssetPath(mesh);
                    if (!string.IsNullOrEmpty(path)) missingUv2.Add(path);
                }
            }
            sb.AppendLine($"Static renderers (the lightmapped set): {staticRenderers.Length}.");
            sb.AppendLine(missingUv2.Count == 0
                ? "Lightmap UVs: OK — every STATIC mesh has a UV2 channel."
                : $"Lightmap UVs: *** {missingUv2.Count} STATIC model(s) MISSING UV2 *** — enable " +
                  "'Generate Lightmap UVs' on their importers or the bake will be garbage. First few:\n    " +
                  string.Join("\n    ", missingUv2.Take(8)));

            // Lights
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                               .Where(l => l.gameObject.scene == scene).ToArray();
            sb.AppendLine($"Lights: {lights.Length} total — " +
                          $"{lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime)} Realtime, " +
                          $"{lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked)} Baked, " +
                          $"{lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed)} Mixed; " +
                          $"{lights.Count(l => l.shadows != LightShadows.None)} cast shadows.");
            if (!lights.Any(l => l.type == LightType.Directional))
                sb.AppendLine("  NOTE: no Directional light — until GI is baked the scene falls back to ambient only.");

            // Probes
            var groups = Object.FindObjectsByType<LightProbeGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                               .Where(g => g.gameObject.scene == scene).ToArray();
            int probeCount = groups.Sum(g => g.probePositions != null ? g.probePositions.Length : 0);
            sb.AppendLine(groups.Length == 0
                ? "Light probes: NONE — dynamic avatars will be unlit once lighting is baked."
                : $"Light probes: {groups.Length} group(s), {probeCount} probe(s).");

            var refl = Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                             .Where(p => p.gameObject.scene == scene).ToArray();
            sb.AppendLine($"Reflection probes: {refl.Length} — " +
                          $"{refl.Count(p => p.mode == ReflectionProbeMode.Baked)} Baked, " +
                          $"{refl.Count(p => p.mode == ReflectionProbeMode.Realtime)} Realtime.");

            sb.AppendLine($"Occlusion culling data: {(StaticOcclusionCulling.umbraDataSize > 0 ? "baked" : "NOT baked")}.");
            sb.AppendLine($"Lightmaps currently in scene: {LightmapSettings.lightmaps?.Length ?? 0}.");

            Debug.Log(sb.ToString());
        }

        // ============================================================================================
        // 2. PREP — static flags + light bake types + reflection probes + probe grid
        // ============================================================================================

        [MenuItem("Tools/Khela/World Prep/2 - Prep Open Scene", priority = 21)]
        public static void PrepOpenScene()
        {
            var scene = SceneManager.GetActiveScene();
            int marked = MarkStatic(scene);
            int lights = ConvertLights(scene);
            int refl   = BakeReflectionProbes(scene);
            int probes = GenerateProbeGrid(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[WorldPrep] {scene.name}: {marked} object(s) marked static, {lights} light(s) -> Baked " +
                      $"(shadow settings untouched by design), {refl} reflection probe(s) -> Baked, " +
                      $"{probes} light probe(s) placed.\n" +
                      "Next: Tools ▸ Khela ▸ Bake Virtual-World Lighting, then World Prep ▸ 3 - Bake Occlusion Culling.");
        }

        [MenuItem("Tools/Khela/World Prep/3 - Bake Occlusion Culling", priority = 22)]
        public static void BakeOcclusion()
        {
            Debug.Log("[WorldPrep] Computing occlusion culling …");
            StaticOcclusionCulling.Compute();
            Debug.Log($"[WorldPrep] Occlusion culling done — umbra data {StaticOcclusionCulling.umbraDataSize} bytes.");
        }

        [MenuItem("Tools/Khela/World Prep/Prep ALL Virtual-World Scenes", priority = 40)]
        public static void PrepAllScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string restore = SceneManager.GetActiveScene().path;

            var guids = AssetDatabase.FindAssets("t:Scene", new[] { Root });
            if (guids.Length == 0) { Debug.LogWarning($"[WorldPrep] No scenes under {Root}."); return; }

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("World Prep", $"{path}  ({i + 1}/{guids.Length})",
                                                     (float)i / guids.Length);
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    PrepOpenScene();
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (!string.IsNullOrEmpty(restore)) EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
            Debug.Log($"[WorldPrep] Prepped {guids.Length} scene(s). Now run Tools ▸ Khela ▸ Bake Virtual-World Lighting.");
        }

        // ============================================================================================
        // internals
        // ============================================================================================

        /// <summary>Objects that must stay dynamic — skipped along with their whole subtree.</summary>
        private static bool IsDynamic(GameObject go) =>
            go.GetComponent<Animator>() != null ||
            go.GetComponent<Animation>() != null ||
            go.GetComponent<Rigidbody>() != null ||
            go.GetComponent<CharacterController>() != null ||
            go.GetComponent<ParticleSystem>() != null ||
            go.GetComponent<Canvas>() != null ||
            go.GetComponent<Camera>() != null ||
            go.GetComponent<UnityEngine.AI.NavMeshAgent>() != null;

        /// <summary>Walks the scene, visiting every GameObject that should be treated as static environment.</summary>
        private static void ForEachPreppable(Scene scene, System.Action<GameObject> visit, System.Action onSkip = null)
        {
            void Walk(Transform t)
            {
                if (IsDynamic(t.gameObject)) { onSkip?.Invoke(); return; }   // skip subtree
                if (t.GetComponent<Light>() == null)                         // lights handled separately
                    visit(t.gameObject);
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i));
            }

            foreach (var root in scene.GetRootGameObjects()) Walk(root.transform);
        }

        private static int MarkStatic(Scene scene)
        {
            int n = 0;
            ForEachPreppable(scene, go =>
            {
                var flags = StaticEditorFlags.ContributeGI
                          | StaticEditorFlags.BatchingStatic
                          | StaticEditorFlags.OccludeeStatic
                          | StaticEditorFlags.ReflectionProbeStatic
                          | StaticEditorFlags.NavigationStatic
                          | StaticEditorFlags.OffMeshLinkGeneration;

                // Transparent geometry (glass, fog cards, neon FX) must NOT occlude — marking it an occluder
                // would cull whatever is visible behind it.
                if (!HasTransparentMaterial(go)) flags |= StaticEditorFlags.OccluderStatic;

                if (GameObjectUtility.GetStaticEditorFlags(go) == flags) return;
                Undo.RecordObject(go, "World Prep: mark static");
                GameObjectUtility.SetStaticEditorFlags(go, flags);
                n++;
            });
            return n;
        }

        private static bool HasTransparentMaterial(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return false;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.renderQueue >= (int)RenderQueue.Transparent) return true;
            return false;
        }

        private static int ConvertLights(Scene scene)
        {
            int n = 0;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (l.gameObject.scene != scene || l.lightmapBakeType == LightmapBakeType.Baked) continue;
                Undo.RecordObject(l, "World Prep: light -> Baked");
                l.lightmapBakeType = LightmapBakeType.Baked;
                // Shadow settings intentionally NOT touched — see class docs.
                EditorUtility.SetDirty(l);
                n++;
            }
            return n;
        }

        private static int BakeReflectionProbes(Scene scene)
        {
            int n = 0;
            foreach (var p in Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (p.gameObject.scene != scene || p.mode == ReflectionProbeMode.Baked) continue;
                Undo.RecordObject(p, "World Prep: reflection probe -> Baked");
                p.mode = ReflectionProbeMode.Baked;
                EditorUtility.SetDirty(p);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Builds a light-probe grid over the scene's renderer bounds. Probes are dropped onto whatever floor a
        /// downward raycast finds, at ankle/body/head heights, and any probe buried inside geometry is discarded.
        /// Falls back to a flat grid if the scene has no colliders to trace against.
        /// </summary>
        private static int GenerateProbeGrid(Scene scene)
        {
            var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                                  .Where(r => r.gameObject.scene == scene).ToArray();
            if (renderers.Length == 0) { Debug.LogWarning("[WorldPrep] No renderers — skipped probe grid."); return 0; }

            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            var positions = new List<Vector3>();
            float top = bounds.max.y + 1f;
            float rayLen = bounds.size.y + 2f;
            bool anyFloorHit = false;

            for (float x = bounds.min.x; x <= bounds.max.x && positions.Count < MaxProbes; x += ProbeSpacing)
            for (float z = bounds.min.z; z <= bounds.max.z && positions.Count < MaxProbes; z += ProbeSpacing)
            {
                // RaycastAll + lowest hit, NOT a single Raycast: this is an interior, so a downward ray from
                // above hits the CEILING first. The floor is the lowest surface under this column.
                var hits = Physics.RaycastAll(new Vector3(x, top, z), Vector3.down, rayLen);
                if (hits.Length == 0) continue;   // nothing underneath — outside the room
                float floorY = hits.Min(h => h.point.y);
                anyFloorHit = true;

                foreach (var h in ProbeHeights)
                {
                    var p = new Vector3(x, floorY + h, z);
                    if (p.y > bounds.max.y) continue;
                    if (Physics.CheckSphere(p, ProbeClearance)) continue;   // buried in geometry
                    positions.Add(p);
                }
            }

            // No colliders at all? Fall back to a plain volume grid so avatars at least get *something*.
            if (!anyFloorHit)
            {
                Debug.LogWarning("[WorldPrep] No colliders hit — falling back to a flat probe grid. " +
                                 "Add colliders for a better-fitted grid.");
                for (float x = bounds.min.x; x <= bounds.max.x && positions.Count < MaxProbes; x += ProbeSpacing)
                for (float z = bounds.min.z; z <= bounds.max.z && positions.Count < MaxProbes; z += ProbeSpacing)
                foreach (var h in ProbeHeights)
                    positions.Add(new Vector3(x, bounds.min.y + h, z));
            }

            if (positions.Count == 0) { Debug.LogWarning("[WorldPrep] Probe grid produced 0 probes."); return 0; }

            // Replace any previously generated group so re-running is idempotent.
            var existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == ProbeGroupName);
            if (existing != null) Undo.DestroyObjectImmediate(existing);

            var go = new GameObject(ProbeGroupName);
            Undo.RegisterCreatedObjectUndo(go, "World Prep: light probe grid");
            go.transform.position = Vector3.zero;    // probePositions are LOCAL — identity keeps them world-space
            var group = Undo.AddComponent<LightProbeGroup>(go);
            group.probePositions = positions.ToArray();

            if (positions.Count >= MaxProbes)
                Debug.LogWarning($"[WorldPrep] Probe cap ({MaxProbes}) hit — increase ProbeSpacing for full coverage.");

            return positions.Count;
        }
    }
}
#endif

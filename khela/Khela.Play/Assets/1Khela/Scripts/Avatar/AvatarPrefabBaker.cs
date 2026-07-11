using System.Collections;
using Bozo.ModularCharacters;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PlayCard.Avatar
{
    /// <summary>
    /// One-press EDITOR baker: turns every <see cref="AvatarConfig"/> roster base into a STATIC BoZo prefab (mesh +
    /// textures baked, <c>isStatic</c> = true → NO runtime merge, no cloth sim, no hang) and auto-assigns it to that
    /// roster entry's <c>displayPrefab</c>. Then the carousel shows the baked prefabs with zero merge cost.
    ///
    /// HOW: add this to a GameObject in a scratch scene, assign Config + a BSMC actor prefab, press <b>Play</b>, then run
    /// the context-menu <b>Bake All Avatars</b>. It bakes ONE character at a time (the demo-safe merge pattern — never the
    /// multi-merge storm that freezes the editor), saves each, assigns it, and resumes past any already-baked entry.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarPrefabBaker : MonoBehaviour
    {
        [SerializeField] private AvatarConfig config;
        [Tooltip("A BSMC actor prefab (has an OutfitSystem) — the template each character is baked from.")]
        [SerializeField] private GameObject actorPrefab;
        [Tooltip("Re-bake entries that already have a displayPrefab (off = skip them, so a re-run resumes).")]
        [SerializeField] private bool rebakeExisting = false;
        [Tooltip("Spread each merge over frames. Leave off for baking (synchronous completes more predictably).")]
        [SerializeField] private bool asyncMerge = false;
        [Tooltip("Async-completion timeout per character. NOTE: this can NOT stop a synchronous merge freeze — if the " +
                 "editor locks up, the last 'merging …' log line names the culprit base; remove it and re-run.")]
        [SerializeField] private float mergeTimeoutSeconds = 25f;
        [SerializeField] private float saveTimeoutSeconds = 20f;

        [ContextMenu("Bake All Avatars (enter Play first)")]
        public void BakeAll()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { Debug.LogWarning("[AvatarBaker] Enter PLAY mode first, then run Bake All Avatars."); return; }
            if (config == null || actorPrefab == null) { Debug.LogWarning("[AvatarBaker] Assign Config + Actor Prefab."); return; }
            StopAllCoroutines();
            StartCoroutine(BakeRoutine());
#else
            Debug.LogWarning("[AvatarBaker] Editor only.");
#endif
        }

        /// <summary>Fix ALREADY-baked prefabs: strip Magica Cloth from every roster displayPrefab (no re-bake, no Play).</summary>
        [ContextMenu("Strip Cloth From Baked Prefabs (fixes existing)")]
        public void StripClothFromBaked()
        {
#if UNITY_EDITOR
            if (config == null) { Debug.LogWarning("[AvatarBaker] Assign Config."); return; }
            int comps = 0, prefabs = 0;
            foreach (var choice in config.roster)
            {
                if (choice?.displayPrefab == null) continue;
                int n = StripClothFromPrefab(choice.displayPrefab);
                if (n > 0) { comps += n; prefabs++; }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[AvatarBaker] Stripped {comps} Magica components from {prefabs} baked prefabs.");
#else
            Debug.LogWarning("[AvatarBaker] Editor only.");
#endif
        }

#if UNITY_EDITOR
        private IEnumerator BakeRoutine()
        {
            // Pre-flight: if BoZo's prefab folder is unset, every SaveCharacterToPrefab silently writes nothing.
            if (string.IsNullOrEmpty(CharacterToolSettingsProvider.Get().prefabFolder))
            {
                Debug.LogError("[AvatarBaker] BoZo CharacterToolSettings 'Prefab Folder' is unassigned (Tools ▸ BoZo Tools ▸ Settings) — every save would silently fail. Set it, then re-run.");
                yield break;
            }

            int baked = 0, total = config.roster.Count;
            for (int i = 0; i < total; i++)
            {
                var choice = config.roster[i];
                if (choice == null || string.IsNullOrEmpty(choice.id)) continue;
                if (!rebakeExisting && choice.displayPrefab != null)
                {
                    Debug.Log($"[AvatarBaker] skip {choice.id} (already baked).");
                    continue;
                }

                string saveName = "Baked_" + SafeName(choice);
                Debug.Log($"[AvatarBaker] ({i + 1}/{total}) merging {choice.id} → {saveName} … (if the editor FREEZES here, THIS base is the culprit — remove it from the roster and re-run)");

                // Copy-safe base (never mutates the shared Resources asset).
                var data = AvatarService.LoadBaseData(choice.id);
                if (data == null) { Debug.LogWarning($"[AvatarBaker] base not found: {choice.id}"); continue; }

                // Fresh actor per bake — SaveCharacterToPrefab makes the actor static + strips it, so never reuse one.
                var go = Instantiate(actorPrefab);
                var os = go.GetComponentInChildren<OutfitSystem>(true);
                if (os == null) { Debug.LogError("[AvatarBaker] actorPrefab has no OutfitSystem."); Destroy(go); continue; }

                // BoZo's SaveCharacterToPrefab calls animator.Rebind() with no null-check; the base prefab ships without
                // an Animator, so add an empty one to avoid an NRE that would fail the bake. (Assign the Humanoid Avatar +
                // Avatar_Idle_Controller on the actor prefab beforehand if you want the baked characters to idle.)
                if (go.GetComponentInChildren<Animator>(true) == null) go.AddComponent<Animator>();

                // If the actor prefab auto-loads on Awake (loadMode OnStart*) it's already merging; our explicit load
                // would hit BoZo's `if (isloading) return;` and never fire OnCharacterLoaded → guaranteed timeout.
                if (os.isloading)
                {
                    Debug.LogError($"[AvatarBaker] {saveName}: actor auto-loaded on Awake — set the actor prefab's OutfitSystem Load Mode = Manual, then re-run. Skipped.");
                    Destroy(go);
                    continue;
                }

                bool loaded = false;
                UnityEngine.Events.UnityAction onLoaded = () => loaded = true;
                os.OnCharacterLoaded += onLoaded;

                os.async = asyncMerge;
                os.prefabName = saveName;
                os.loadMode = OutfitSystem.LoadMode.Manual;
                var loadTask = BMAC_SaveSystem.LoadCharacter(os, data);   // load + merge (we wait on OnCharacterLoaded)

                float t = 0f;
                while (!loaded && !loadTask.IsFaulted && t < mergeTimeoutSeconds) { t += Time.unscaledDeltaTime; yield return null; }
                os.OnCharacterLoaded -= onLoaded;
                if (loadTask.IsFaulted)
                {
                    Debug.LogError($"[AvatarBaker] {saveName} load threw: {loadTask.Exception?.GetBaseException().Message}");
                    Destroy(go);
                    continue;
                }
                if (!loaded) { Debug.LogWarning($"[AvatarBaker] {saveName} didn't finish within {mergeTimeoutSeconds}s — skipped."); Destroy(go); continue; }

                yield return null;               // let the final merged frame settle
                os.SaveCharacterToPrefab();      // async void → poll the AssetDatabase for the result

                GameObject prefab = null;
                float s = 0f;
                while (prefab == null && s < saveTimeoutSeconds)
                {
                    s += Time.unscaledDeltaTime;
                    yield return null;
                    prefab = FindBakedPrefab(saveName);
                }

                Destroy(go);

                if (prefab == null)
                {
                    Debug.LogWarning($"[AvatarBaker] {saveName} not found after save (see the 'Saved Prefab at:' log for the path).");
                    continue;
                }

                StripClothFromPrefab(prefab);   // a static/combined character can't build cloth (MC2 20409) — remove it
                choice.displayPrefab = prefab;
                EditorUtility.SetDirty(config);
                baked++;
                Debug.Log($"[AvatarBaker] assigned {saveName} → roster[{i}].displayPrefab");
                yield return new WaitForSeconds(0.25f);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[AvatarBaker] DONE — {baked} baked/assigned. Exit Play, then on AvatarCarousel: Clear → Build, and set Load Bases At Runtime = false.");
        }

        private static GameObject FindBakedPrefab(string saveName)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{saveName} t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == saveName)
                    return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            return null;
        }

        /// <summary>Remove runtime-only BoZo components from a baked prefab: Magica Cloth (a static/combined character
        /// can't build cloth — MC2 error 20409) and <see cref="OutfitSystem"/> itself (an isStatic body can't swap
        /// outfits anyway, and its OnDestroy calls copyTexture.Release() which NREs on every destroyed baked instance).
        /// Returns how many components were removed.</summary>
        public static int StripClothFromPrefab(GameObject prefabAsset)
        {
            if (prefabAsset == null) return 0;
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path)) return 0;

            var root = PrefabUtility.LoadPrefabContents(path);
            int removed = 0;
            foreach (var comp in root.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                // Type-name match (no asmdef reference needed): MagicaCloth/MagicaColliders/BoZo_MagicaClothSupport + OutfitSystem.
                if (n.Contains("Magica") || n == "OutfitSystem")
                {
                    Object.DestroyImmediate(comp, true);
                    removed++;
                }
            }
            if (removed > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            return removed;
        }
#endif

        private static string SafeName(AvatarConfig.BaseChoice c)
        {
            string n = string.IsNullOrEmpty(c.displayName) ? c.id : c.displayName;
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(bad, '_');
            return n.Replace('/', '_').Replace(" ", "");
        }
    }
}

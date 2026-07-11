using System.Text;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Shows the signed-in player's saved avatar on a 3D stage (the Home/profile <c>AvatarStage</c>), replacing the
    /// placeholder dummy. Two render paths:
    ///  • Selection-only avatar (no shape/outfit diff) → the base's BAKED display prefab from <see cref="AvatarConfig"/> —
    ///    instant and merge-free (same prefabs the onboarding carousel shows).
    ///  • Customized avatar (wardrobe diff present) → a LIVE BoZo actor (<see cref="actorPrefab"/>) merged with the full
    ///    diff via <see cref="AvatarService.BuildCharacter"/> — one on-demand merge, same cost as opening the wardrobe.
    /// Re-renders automatically when the avatar changes (<see cref="AvatarService.MineChanged"/>), including same-base
    /// wardrobe saves (change detection is a content signature, not just the baseId). Falls back to the placeholder when
    /// there's no avatar / no prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarStageBinder : MonoBehaviour
    {
        [Tooltip("The roster asset — maps the saved baseId to its baked display prefab.")]
        [SerializeField] private AvatarConfig config;
        [Tooltip("Where the character is spawned (position/rotation/scale come from this). E.g. the stage's Mount.")]
        [SerializeField] private Transform mount;
        [Tooltip("The dummy model currently on the stage — hidden once the real avatar shows, shown again as fallback.")]
        [SerializeField] private GameObject placeholder;
        [Tooltip("Live BSMC actor prefab (has an OutfitSystem) — used when the avatar has wardrobe customization. " +
                 "Without it, customized avatars fall back to the baked base look.")]
        [SerializeField] private GameObject actorPrefab;

        private GameObject _instance;
        private string _shownSignature;
        private int _seq;   // stale-continuation guard: only the newest Rebind may finish

        private async void OnEnable()
        {
            AvatarService.Instance.MineChanged += Rebind;
            // Boot normally seeded Mine already; fetch only if we somehow got here without it.
            if (AvatarService.Instance.Mine == null) await AvatarService.Instance.LoadMineAsync();
            Rebind();
        }

        private void OnDisable() => AvatarService.Instance.MineChanged -= Rebind;

        private async void Rebind()
        {
            var mine = AvatarService.Instance.Mine;
            if (mine == null || string.IsNullOrEmpty(mine.BaseId)) { ShowPlaceholder(); return; }

            string signature = Signature(mine);
            if (_instance != null && _shownSignature == signature) return;   // already showing exactly this avatar

            bool customized = HasCustomization(mine) && actorPrefab != null;
            var prefab = customized ? actorPrefab : FindDisplayPrefab(mine.BaseId);
            if (prefab == null)
            {
                Debug.LogWarning($"[AvatarStageBinder] no {(customized ? "actor" : "baked display")} prefab for '{mine.BaseId}' — keeping the placeholder.");
                ShowPlaceholder();
                return;
            }

            int seq = ++_seq;
            if (_instance != null) Destroy(_instance);
            var parent = mount != null ? mount : transform;
            _instance = Instantiate(prefab, parent);
            _instance.transform.localPosition = Vector3.zero;
            _instance.transform.localRotation = Quaternion.identity;
            _instance.transform.localScale = Vector3.one;
            _shownSignature = signature;

            if (customized)
            {
                var os = _instance.GetComponentInChildren<OutfitSystem>(true);
                var data = AvatarService.BuildCharacter(mine);
                if (os != null && data != null)
                {
                    os.loadMode = OutfitSystem.LoadMode.Manual;   // before its Start — we drive the (async) merge ourselves
                    os.async = true;
                    try { await BMAC_SaveSystem.LoadCharacter(os, data); }
                    catch (System.Exception e) { Debug.LogError($"[AvatarStageBinder] merge failed: {e.Message}"); }
                    if (this == null || seq != _seq || _instance == null) return;   // superseded/torn down mid-merge
                }
                else Debug.LogWarning("[AvatarStageBinder] actor prefab has no OutfitSystem or avatar didn't build — showing it unmerged.");
            }

            // Stage cameras cull a dedicated layer — set it AFTER the merge so the merge-created meshes get it too.
            int layer = placeholder != null ? placeholder.layer : parent.gameObject.layer;
            SetLayerRecursively(_instance, layer);

            if (placeholder != null) placeholder.SetActive(false);
            Debug.Log($"[AvatarStageBinder] showing '{mine.BaseId}' ({(customized ? "customized, live-merged" : "baked")}) on layer {LayerMask.LayerToName(layer)}.");
        }

        /// <summary>Any wardrobe diff beyond the bare base pick?</summary>
        private static bool HasCustomization(AvatarData a)
            => (a.Outfits != null && a.Outfits.Count > 0) ||
               (a.Body != null && a.Body.Count > 0) ||
               (a.Face != null && a.Face.Count > 0) ||
               (a.Mods != null && a.Mods.Count > 0);

        // Content signature so a same-base wardrobe save still re-renders (baseId alone misses outfit/shape changes).
        private static string Signature(AvatarData a)
        {
            var sb = new StringBuilder(a.BaseId).Append('|').Append(a.Gender);
            if (a.Body != null) foreach (var s in a.Body) sb.Append('|').Append(s.Key).Append(':').Append(s.Value);
            if (a.Face != null) foreach (var s in a.Face) sb.Append('|').Append(s.Key).Append(':').Append(s.Value);
            if (a.Mods != null) foreach (var m in a.Mods)
                sb.Append('|').Append(m.Bone).Append(':').Append(m.Scale).Append(',').Append(m.Sx).Append(',').Append(m.Sy)
                  .Append(',').Append(m.Sz).Append(',').Append(m.Px).Append(',').Append(m.Py).Append(',').Append(m.Pz);
            if (a.Outfits != null) foreach (var o in a.Outfits)
            {
                sb.Append('|').Append(o.Path);
                if (o.Colors != null) foreach (var c in o.Colors) sb.Append(',').Append(c);
            }
            return sb.ToString();
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
        }

        private GameObject FindDisplayPrefab(string baseId)
        {
            if (config == null) return null;
            foreach (var b in config.roster)
                if (b != null && b.id == baseId) return b.displayPrefab;
            return null;
        }

        private void ShowPlaceholder()
        {
            if (_instance != null) { Destroy(_instance); _instance = null; _shownSignature = null; }
            if (placeholder != null) placeholder.SetActive(true);
        }
    }
}

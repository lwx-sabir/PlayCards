using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Avatar
{
    using Gender = AvatarConfig.Gender;
    using ShapeLimit = AvatarConfig.ShapeLimit;
    using ShapeKind = AvatarConfig.ShapeKind;
    using ShapeCategory = AvatarConfig.ShapeCategory;
    using BaseChoice = AvatarConfig.BaseChoice;

    /// <summary>
    /// The curated PLAYER avatar creator (NOT BoZo's dev creator). Drives a live <see cref="OutfitSystem"/> through the
    /// rules in <see cref="AvatarConfig"/>: the player picks a gender + premade base, then every edit is CLAMPED to the
    /// per-parameter limits (so a girl can't be flat, ears/eyes can't go animal, the gender axis is locked). Dress and
    /// facial slots are free; only deformation is bounded. On <see cref="SaveAsync"/> it snapshots the rig and pushes it
    /// to the server (the source of truth, which re-sanitizes).
    ///
    /// This is UI-agnostic: your HUD binds sliders/buttons to these methods. Bind a slider's 0..1 to
    /// <see cref="GetNormalized"/> / <see cref="SetNormalized"/> for each <see cref="EditableShapes"/> entry.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarCreator : MonoBehaviour
    {
        [Tooltip("The rules/limits asset (Khela ▸ Avatar Config).")]
        [SerializeField] private AvatarConfig config;

        [Tooltip("The live BoZo actor being edited. If empty, searched on this GameObject / children at Awake.")]
        [SerializeField] private OutfitSystem outfitSystem;

        /// <summary>Raised after the rig changes wholesale (base loaded / saved avatar applied) so the UI re-reads sliders.</summary>
        public event Action OnAvatarChanged;

        public AvatarConfig Config => config;
        public OutfitSystem Outfit => outfitSystem;
        public Gender CurrentGender { get; private set; } = Gender.Male;
        public string CurrentBaseId { get; private set; }
        public bool IsBusy { get; private set; }

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private AvatarConfig.GenderProfile Profile => config.Profile(CurrentGender);

        // ---- base / gender selection ----

        /// <summary>The bases the player may choose for a gender (from the config roster).</summary>
        public List<BaseChoice> BasesFor(Gender g)
            => config != null ? config.roster.Where(b => b.gender == g).ToList() : new List<BaseChoice>();

        /// <summary>Load a premade base onto the rig, enforce the locked gender axis, and pull every value into its limits
        /// (so no base violates the curated floors/ceilings). Awaitable — the rig is ready when it returns.</summary>
        public async Task LoadBaseAsync(Gender gender, string baseId)
        {
            if (outfitSystem == null || config == null) { Debug.LogWarning("[AvatarCreator] not configured."); return; }
            var baseData = AvatarService.LoadBaseData(baseId);
            if (baseData == null) return;

            CurrentGender = gender;
            CurrentBaseId = baseId;

            IsBusy = true;
            try
            {
                await BMAC_SaveSystem.LoadCharacter(outfitSystem, baseData);
                EnforceLimits();   // lock gender axis + clamp any out-of-range base value into the curated band
            }
            catch (Exception e) { Debug.LogError($"[AvatarCreator] base load failed: {e.Message}"); }
            finally { IsBusy = false; }

            OnAvatarChanged?.Invoke();
        }

        /// <summary>Load the player's SAVED avatar into the creator so they resume editing it (falls back to a base).</summary>
        public async Task LoadSavedOrBaseAsync(Gender fallbackGender, string fallbackBaseId)
        {
            var mine = AvatarService.Instance.Mine ?? await AvatarService.Instance.LoadMineAsync();
            if (mine == null || string.IsNullOrEmpty(mine.BaseId)) { await LoadBaseAsync(fallbackGender, fallbackBaseId); return; }

            var data = AvatarService.BuildCharacter(mine);
            if (data == null) { await LoadBaseAsync(fallbackGender, fallbackBaseId); return; }

            CurrentGender = string.Equals(mine.Gender, "Female", StringComparison.OrdinalIgnoreCase) ? Gender.Female : Gender.Male;
            CurrentBaseId = mine.BaseId;

            IsBusy = true;
            try
            {
                await BMAC_SaveSystem.LoadCharacter(outfitSystem, data);
                EnforceLimits();
            }
            catch (Exception e) { Debug.LogError($"[AvatarCreator] saved load failed: {e.Message}"); }
            finally { IsBusy = false; }

            OnAvatarChanged?.Invoke();
        }

        // ---- shape editing (clamped to the config) ----

        /// <summary>The player-movable shapes for the current gender (locked ones excluded), optionally one category.</summary>
        public List<ShapeLimit> EditableShapes(ShapeCategory? category = null)
            => Profile.shapes.Where(s => !s.locked && (category == null || s.category == category.Value)).ToList();

        /// <summary>Current value of a shape, read live off the rig (for slider init). Falls back to the config default.</summary>
        public float GetValue(ShapeLimit s)
        {
            if (outfitSystem == null || s == null) return s?.def ?? 0f;
            switch (s.kind)
            {
                case ShapeKind.Blendshape:
                    return outfitSystem.GetShape(s.key);
                case ShapeKind.BoneUniform:
                    return Mod(s.key, out var m) ? m.GetData().scaleValue : s.def;
                case ShapeKind.BoneAxis:
                    return Mod(s.key, out var ma) ? Axis(ma.GetData().scale, s.axis) : s.def;
                default: return s.def;
            }
        }

        /// <summary>Current value as 0..1 across [min,max] (for a slider handle).</summary>
        public float GetNormalized(ShapeLimit s)
            => s == null || Mathf.Approximately(s.max, s.min) ? 0f : Mathf.Clamp01(Mathf.InverseLerp(s.min, s.max, GetValue(s)));

        /// <summary>Set from a 0..1 slider → mapped into [min,max], clamped, applied live. Returns the applied value.</summary>
        public float SetNormalized(ShapeLimit s, float t01)
            => SetValue(s, Mathf.Lerp(s.min, s.max, Mathf.Clamp01(t01)));

        /// <summary>Set an absolute value, clamped to the shape's [min,max], applied live. Returns the applied value.</summary>
        public float SetValue(ShapeLimit s, float value)
        {
            if (outfitSystem == null || s == null) return 0f;
            if (s.locked) return s.def;                        // never movable
            float v = Mathf.Clamp(value, s.min, s.max);
            ApplyRaw(s, v);
            return v;
        }

        // Applies a value to the rig by kind, no clamping (callers clamp first).
        private void ApplyRaw(ShapeLimit s, float v)
        {
            switch (s.kind)
            {
                case ShapeKind.Blendshape:
                    outfitSystem.SetShape(s.key, v);
                    break;
                case ShapeKind.BoneUniform:
                    if (Mod(s.key, out var m)) m.SetScale(v);
                    break;
                case ShapeKind.BoneAxis:
                    if (Mod(s.key, out var ma))
                    {
                        var d = ma.GetData();
                        if (s.axis == "x") d.scale.x = v;
                        else if (s.axis == "y") d.scale.y = v;
                        else if (s.axis == "z") d.scale.z = v;
                        ma.SetData(d);
                    }
                    break;
            }
        }

        // Lock the gender axis to its def + clamp everything else into range (called after any wholesale load).
        private void EnforceLimits()
        {
            foreach (var s in Profile.shapes)
            {
                if (s.locked) { ApplyRaw(s, s.def); continue; }
                float cur = GetValue(s);
                float clamped = Mathf.Clamp(cur, s.min, s.max);
                if (!Mathf.Approximately(cur, clamped)) ApplyRaw(s, clamped);
            }
        }

        private bool Mod(string key, out BodyShapeModifier m)
        {
            m = null;
            return outfitSystem.bodyModifiers != null && outfitSystem.bodyModifiers.TryGetValue(key, out m) && m != null;
        }

        private static float Axis(Vector3 v, string axis) => axis == "y" ? v.y : axis == "z" ? v.z : v.x;

        // ---- outfits / colours ----

        /// <summary>Attach an outfit part by Resources path (e.g. "Top/BSMC_Top_Tee"); BoZo swaps same-slot + re-merges.</summary>
        public void SetOutfit(string resourcesPath)
        {
            if (outfitSystem == null || string.IsNullOrEmpty(resourcesPath)) return;
            var outfit = Resources.Load<Outfit>(resourcesPath);
            if (outfit == null) { Debug.LogWarning($"[AvatarCreator] outfit not found: {resourcesPath}"); return; }
            outfitSystem.AttachOutfit(outfit);
        }

        /// <summary>Remove whatever occupies a slot (OutfitType name, e.g. "Hat").</summary>
        public void RemoveOutfitSlot(string slot)
        {
            if (outfitSystem != null && !string.IsNullOrEmpty(slot)) outfitSystem.RemoveOutfit(slot);
        }

        /// <summary>Recolour a slot's channel (1–9) — pass a curated palette swatch.</summary>
        public void SetOutfitColor(string slot, int channel, Color color)
        {
            outfitSystem?.GetOutfit(slot)?.SetColor(color, channel);
        }

        /// <summary>Curated palettes (skin/hair/…) for the UI to render as swatches.</summary>
        public List<AvatarConfig.ColorPalette> Palettes => config != null ? config.palettes : new List<AvatarConfig.ColorPalette>();

        // ---- save ----

        /// <summary>Snapshot the live rig and push it to the server (source of truth). Returns success.</summary>
        public async Task<bool> SaveAsync()
        {
            if (outfitSystem == null) return false;
            var data = BMAC_SaveSystem.GetCharacterData(outfitSystem);
            var avatar = AvatarMapper.FromCharacter(data, CurrentGender.ToString(), CurrentBaseId);
            return await AvatarService.Instance.SaveAsync(avatar);
        }
    }
}

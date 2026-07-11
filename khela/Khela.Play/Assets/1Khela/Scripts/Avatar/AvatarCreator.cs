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

        /// <summary>True after the last load IFF the player's REAL saved avatar was applied. False when we fell back to a
        /// base (none saved, or it couldn't be built). Guards against clobbering a real avatar with a fallback.</summary>
        public bool LoadedSavedAvatar { get; private set; }

        /// <summary>True when the last load found a stored avatar on the server (Mine had a BaseId), whether or not it applied.</summary>
        public bool HadStoredAvatar { get; private set; }

        /// <summary>Saving now would overwrite a REAL stored avatar with a fallback base (i.e. its load failed). The wardrobe
        /// refuses this — a load failure must never destroy the player's saved avatar.</summary>
        public bool SaveWouldClobber => HadStoredAvatar && !LoadedSavedAvatar;

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private AvatarConfig.GenderProfile Profile => config.Profile(CurrentGender);

        // ---- base / gender selection ----

        /// <summary>The genders the config offers bases for (roster-derived) — for a gender toggle.</summary>
        public List<Gender> Genders()
            => config != null ? config.roster.Select(b => b.gender).Distinct().ToList() : new List<Gender>();

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

            LoadedSavedAvatar = false;   // a base/fallback load is never the player's saved avatar
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
            HadStoredAvatar = mine != null && !string.IsNullOrEmpty(mine.BaseId);
            Debug.Log($"[AvatarCreator] LoadSavedOrBase → Mine = {(mine == null ? "NULL (not seeded — did you Play from Boot?)" : $"gender={mine.Gender} base={mine.BaseId} outfits={mine.Outfits?.Count ?? 0}")}");
            if (!HadStoredAvatar)
            {
                Debug.LogWarning($"[AvatarCreator] no saved avatar → falling back to {fallbackBaseId} (first-run: saving here creates the avatar)");
                await LoadBaseAsync(fallbackGender, fallbackBaseId); return;   // LoadedSavedAvatar=false, HadStoredAvatar=false → save allowed
            }

            var data = AvatarService.BuildCharacter(mine);
            if (data == null)
            {
                // We HAVE a stored avatar but couldn't build it — falling back would let a Save overwrite it with the base.
                // Show the base so the screen isn't empty, but SaveWouldClobber stays true so Save is refused.
                Debug.LogError($"[AvatarCreator] BuildCharacter('{mine.BaseId}') returned null — showing fallback but BLOCKING save to protect the stored avatar.");
                await LoadBaseAsync(fallbackGender, fallbackBaseId); return;   // LoadedSavedAvatar=false, HadStoredAvatar=true → save blocked
            }

            CurrentGender = string.Equals(mine.Gender, "Female", StringComparison.OrdinalIgnoreCase) ? Gender.Female : Gender.Male;
            CurrentBaseId = mine.BaseId;

            IsBusy = true;
            try
            {
                await BMAC_SaveSystem.LoadCharacter(outfitSystem, data);
                EnforceLimits();
                LoadedSavedAvatar = true;   // the real saved avatar is on the rig — safe to overwrite on save
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
                    return outfitSystem.GetShapeValue(s.key);   // body THEN face dict (GetShape is body-only → -10000 for Face keys)
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

        /// <summary>Attach an outfit part by Resources path (e.g. "Top/BSMC_Top_Tee"). BoZo swaps same-slot + re-merges.</summary>
        public void SetOutfit(string resourcesPath)
        {
            if (outfitSystem == null || string.IsNullOrEmpty(resourcesPath)) return;
            var prefab = Resources.Load<Outfit>(resourcesPath);
            if (prefab == null) { Debug.LogWarning($"[AvatarCreator] outfit not found: {resourcesPath}"); return; }
            // BoZo's AttachOutfit REPARENTS the object you pass — passing the Resources prefab corrupts/errors. Match
            // BoZo's own creator: instantiate under the rig; the Outfit's Start() self-attaches (GetComponentInParent →
            // AttachOutfit(this)) and merges.
            Instantiate(prefab, outfitSystem.transform);
        }

        /// <summary>Remove whatever occupies a slot (OutfitType name, e.g. "Hat").</summary>
        public void RemoveOutfitSlot(string slot)
        {
            if (outfitSystem != null && !string.IsNullOrEmpty(slot)) outfitSystem.RemoveOutfit(slot);
        }

        /// <summary>Recolour a slot's channel (1-indexed, 1–9) — pass a curated palette swatch.</summary>
        public void SetOutfitColor(string slot, int channel, Color color)
        {
            outfitSystem?.GetOutfit(slot)?.SetColor(color, channel);
        }

        // ---- outfit browsing (curated: config whitelist gates what shows) ----

        /// <summary>One browsable outfit part. <see cref="Path"/> is the Resources key "slot/name" (== the persisted
        /// OutfitData.outfit), so it's the whitelist id, the equip arg, and the save key all at once.</summary>
        public readonly struct OutfitOption
        {
            public readonly string Path;
            public readonly string Label;
            public readonly Sprite Icon;
            public readonly string Slot;
            public OutfitOption(string path, string label, Sprite icon, string slot)
            { Path = path; Label = label; Icon = icon; Slot = slot; }
        }

        private Dictionary<string, List<OutfitOption>> _partsCache;

        // Build the full part index once (Resources.LoadAll is expensive; results never change at runtime).
        private void EnsureParts()
        {
            if (_partsCache != null) return;
            _partsCache = new Dictionary<string, List<OutfitOption>>();

            // MOBILE PATH: read the pre-built index (paths + icon sprites only — NO meshes, NO Resources.LoadAll).
            if (config != null && config.outfitIndex != null && config.outfitIndex.entries.Count > 0)
            {
                foreach (var e in config.outfitIndex.entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.slot) || string.IsNullOrEmpty(e.path)) continue;
                    if (!_partsCache.TryGetValue(e.slot, out var list)) { list = new List<OutfitOption>(); _partsCache[e.slot] = list; }
                    if (list.Any(o => o.Path == e.path)) continue;
                    list.Add(new OutfitOption(e.path, string.IsNullOrEmpty(e.label) ? e.path : e.label, e.icon, e.slot));
                }
                return;
            }

            // FALLBACK (editor convenience only): scan Resources — HEAVY, loads every outfit mesh into RAM. Never ship this.
            Debug.LogWarning("[AvatarCreator] No OutfitIndex on the AvatarConfig — scanning Resources (loads every outfit mesh, phone-hostile). Run Khela ▸ Avatar ▸ Build Outfit Index.");
            foreach (var outfit in Resources.LoadAll<Outfit>(""))
            {
                if (outfit == null || !outfit.showCharacterCreator || outfit.Type == null || string.IsNullOrEmpty(outfit.Type.name)) continue;
                string slot = outfit.Type.name;
                string path = slot + "/" + outfit.name;
                if (!_partsCache.TryGetValue(slot, out var list)) { list = new List<OutfitOption>(); _partsCache[slot] = list; }
                if (list.Any(o => o.Path == path)) continue;
                string label = string.IsNullOrEmpty(outfit.OutfitName) ? outfit.name : outfit.OutfitName;
                list.Add(new OutfitOption(path, label, outfit.OutfitIcon, slot));
            }
        }

        /// <summary>Slots to show, in config order (or all discovered slots if none configured). Empty if no config.</summary>
        public List<string> Slots()
        {
            EnsureParts();
            if (config != null && config.slots != null && config.slots.Count > 0)
                return config.slots.Where(s => s != null && _partsCache.ContainsKey(s.slot)).Select(s => s.slot).ToList();
            return _partsCache.Keys.OrderBy(k => k).ToList();
        }

        /// <summary>Curated parts for a slot — discovered parts gated by the config whitelist (empty ⇒ all). Never null.</summary>
        public List<OutfitOption> PartsForSlot(string slot)
        {
            EnsureParts();
            if (config != null && !config.SlotShown(slot)) return new List<OutfitOption>();
            if (!_partsCache.TryGetValue(slot, out var all)) return new List<OutfitOption>();
            if (config == null) return new List<OutfitOption>(all);
            return all.Where(o => config.PartAllowed(slot, o.Path)).ToList();
        }

        /// <summary>The equipped part path ("slot/name") in a slot, or null if empty/none — for tile highlight.
        /// Null/empty slot returns null (BoZo's GetOutfit throws on a null key).</summary>
        public string CurrentPartPath(string slot)
            => string.IsNullOrEmpty(slot) ? null
             : outfitSystem?.GetOutfit(slot) is { } o ? o.GetOutfitData().outfit : null;

        // ---- colour palettes (curated; route target → equipped outfit channel) ----

        /// <summary>Curated palettes (skin/hair/…) for the UI to render as swatches.</summary>
        public List<AvatarConfig.ColorPalette> Palettes => config != null ? config.palettes : new List<AvatarConfig.ColorPalette>();

        /// <summary>Apply a palette swatch to its configured (slot, channel) + any linkedSlots with the same swatch
        /// (e.g. skin recolours Body AND Head). No-op for slots that are empty. Silent.</summary>
        public void ApplyPalette(AvatarConfig.ColorPalette palette, Color swatch)
        {
            if (palette == null) return;
            SetOutfitColor(palette.slot, palette.channel, swatch);
            if (palette.linkedSlots != null)
                foreach (var linked in palette.linkedSlots)
                    SetOutfitColor(linked, palette.channel, swatch);
        }

        /// <summary>Current colour of a palette's target (for swatch highlight), or null if that slot is empty.</summary>
        public Color? CurrentPaletteColor(AvatarConfig.ColorPalette palette)
            => palette != null && outfitSystem?.GetOutfit(palette.slot) is { } o ? o.GetColor(palette.channel) : (Color?)null;

        /// <summary>The equipped outfit's REAL colour-channel count for a slot (0 if empty) — clamp the swatch UI to this.</summary>
        public int ChannelCount(string slot)
            => outfitSystem?.GetOutfit(slot) is { } o ? (o.ColorChannels != null ? o.ColorChannels.Length : 0) : 0;

        // ---- save ----

        /// <summary>Snapshot the live rig and push it to the server (source of truth). Returns success.</summary>
        public async Task<bool> SaveAsync()
        {
            if (outfitSystem == null) return false;
            if (SaveWouldClobber)   // stored avatar failed to load — refuse rather than overwrite it with the fallback base
            {
                Debug.LogError("[AvatarCreator] SaveAsync refused: your saved avatar didn't load, so saving now would overwrite it with a fallback base.");
                return false;
            }
            var data = BMAC_SaveSystem.GetCharacterData(outfitSystem);
            var avatar = AvatarMapper.FromCharacter(data, CurrentGender.ToString(), CurrentBaseId);
            return await AvatarService.Instance.SaveAsync(avatar);
        }
    }
}

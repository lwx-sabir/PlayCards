using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PlayCard.Avatar
{
    /// <summary>
    /// The dev-editable rules for the PLAYER avatar creator over BoZo. Curated + constrained (NOT the dev creator):
    /// a gender-locked base, per-parameter min/max/default (gender-specific, with tight clamps on the "animal-risk"
    /// params), curated color palettes, and a slot whitelist. Everything a player can do is data here — add a base,
    /// tighten a range, or swap a palette by editing this asset (no recompile).
    ///
    /// References are STRING ids (Resources paths) so this asset has ZERO dependency on BoZo's C# types: bases load via
    /// <c>Resources.Load(baseId)</c> (e.g. "Base/1DefaultMale"), parts via "Top/BSMC_Top_...". The AvatarCreator resolves
    /// them at runtime. First-cut limits come from mining BoZo's 9 premade humans (see <see cref="PopulateFromResearch"/>).
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Avatar Config", fileName = "AvatarConfig")]
    public sealed class AvatarConfig : ScriptableObject
    {
        public enum Gender { Male, Female }

        /// <summary>Blendshape = OutfitSystem.SetShape(key,0-100). BoneUniform = BodyShapeModifier.scaleValue (~1.0).
        /// BoneAxis = a single per-axis scale on a bone (e.g. eyeRoot x/y — the "animal eyes" control).</summary>
        public enum ShapeKind { Blendshape, BoneUniform, BoneAxis }
        public enum ShapeCategory { Body, Face, BodyMod, Eyes }

        [Serializable]
        public sealed class ShapeLimit
        {
            public string key;              // BoZo shape name (blendshape) or bone name (mod)
            public string label;            // player-facing label
            public ShapeKind kind = ShapeKind.Blendshape;
            public ShapeCategory category = ShapeCategory.Body;
            public string axis = "";        // "x"/"y"/"z" for BoneAxis, else empty
            public float min;
            public float max = 100f;
            public float def;
            [Tooltip("Animal/elf-risk param — clamp tight, tint a warning in UI.")]
            public bool deformRisk;
            [Tooltip("Not a player slider (e.g. the gender axis). Fixed at def.")]
            public bool locked;
        }

        [Serializable]
        public sealed class GenderProfile
        {
            public Gender gender;
            [Tooltip("Resources path of the base CharacterObject, e.g. \"Base/1DefaultMale\".")]
            public string baseId;
            public List<ShapeLimit> shapes = new List<ShapeLimit>();
        }

        [Serializable]
        public sealed class BaseChoice
        {
            public string id;               // Resources path, e.g. "Base/Mary"
            public string displayName;
            public Gender gender;

            [Tooltip("OPTIONAL: a ready-to-show character prefab (baked / pre-dressed). If set, the carousel shows it " +
                     "directly with NO runtime merge — fast, distinct, and visible in-editor. If empty, the generic actor " +
                     "prefab is used and this base is merged onto it at runtime (one at a time).")]
            public GameObject displayPrefab;
        }

        [Serializable]
        public sealed class SlotConfig
        {
            public string slot;             // OutfitType name (Top/Bottom/HairFront/…) — the PRIMARY slot + tab label/icon source
            public string label;            // player-facing tab label (empty = use the raw slot name)
            public string group;            // top-level group this tab lives under (Body/Face/Outfit) — drives the group bar
            [Tooltip("Extra OutfitType names merged into this ONE tab, e.g. Underwear tab = UnderLower + UnderUpper. Items " +
                     "from all of them show together; each equips into its own real slot (from its path).")]
            public List<string> extraSlots = new List<string>();
            [Tooltip("Resources paths of allowed parts, e.g. \"Top/BSMC_Top_Tee\". EMPTY = allow every part in this slot.")]
            public List<string> allowedPartIds = new List<string>();

            /// <summary>Every OutfitType this tab pulls items from — the primary slot plus any merged extras.</summary>
            public IEnumerable<string> AllSlots()
            {
                yield return slot;
                if (extraSlots != null) foreach (var s in extraSlots) if (!string.IsNullOrEmpty(s)) yield return s;
            }
        }

        [Serializable]
        public sealed class ColorPalette
        {
            public string target;           // display label: Skin / Hair / Top / …
            [Tooltip("OutfitType name this palette recolours (\"Body\" for skin, \"Top\", \"HairFront\"…).")]
            public string slot;
            [Tooltip("1-indexed colour channel within that outfit (skin is usually the Body outfit, channel 1).")]
            public int channel = 1;
            [Tooltip("Extra slots recoloured with the SAME swatch — e.g. Skin=Body also recolours Head; Hair front+back.")]
            public List<string> linkedSlots = new List<string>();
            public List<Color> swatches = new List<Color>();
        }

        [Header("Bases the player can choose (all premade humans; add/remove freely)")]
        public List<BaseChoice> roster = new List<BaseChoice>();

        [Header("Per-gender customization limits")]
        public GenderProfile male = new GenderProfile { gender = Gender.Male };
        public GenderProfile female = new GenderProfile { gender = Gender.Female };

        [Header("Slot whitelist (empty list on a slot = allow all; omit a slot to hide it)")]
        public List<SlotConfig> slots = new List<SlotConfig>();

        [Header("Curated color palettes (skin → human tones, etc.)")]
        public List<ColorPalette> palettes = new List<ColorPalette>();

        [Header("Outfit catalogue (MOBILE-SAFE) — build via Khela ▸ Avatar ▸ Build Outfit Index. Without it the wardrobe " +
                "falls back to scanning Resources at runtime (loads every mesh into RAM = heavy / phone-hostile).")]
        public OutfitIndex outfitIndex;

        public GenderProfile Profile(Gender g) => g == Gender.Male ? male : female;

        // ---- wardrobe whitelist helpers (drive PartsForSlot; an unauthored config is permissive) ----

        /// <summary>Should this slot appear in the wardrobe? No slots configured ⇒ all shown; else only slots present
        /// in <see cref="slots"/> ("omit a slot to hide it").</summary>
        public bool SlotShown(string slot)
        {
            if (slots == null || slots.Count == 0) return true;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] != null && slots[i].slot == slot) return true;
            return false;
        }

        /// <summary>Player-facing tab label for a slot (its SlotConfig.label, or the raw slot name if unset).</summary>
        public string SlotLabel(string slot)
        {
            if (slots != null)
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i] != null && slots[i].slot == slot && !string.IsNullOrEmpty(slots[i].label))
                        return slots[i].label;
            return slot;
        }

        /// <summary>Is a part ("slot/name") allowed in its slot? No slots configured ⇒ yes (permissive); a configured
        /// slot with an EMPTY allowedPartIds ⇒ every part; a slot omitted from a configured list ⇒ no.</summary>
        public bool PartAllowed(string slot, string path)
        {
            if (slots == null || slots.Count == 0) return true;
            SlotConfig sc = null;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] != null && slots[i].slot == slot) { sc = slots[i]; break; }
            if (sc == null) return false;
            if (sc.allowedPartIds == null || sc.allowedPartIds.Count == 0) return true;
            return sc.allowedPartIds.Contains(path);
        }

        /// <summary>Whitelist-gate a saved outfit path ("slot/name") — used on load/save so a de-listed part can't ride
        /// back in. Splits the path and delegates to <see cref="PartAllowed"/>.</summary>
        public bool PathAllowed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int slash = path.IndexOf('/');
            if (slash <= 0) return false;
            return PartAllowed(path.Substring(0, slash), path);
        }

        // ---- first-cut defaults from the premade-human research (see chat: bozo-avatar-limits-research) ----

        /// <summary>Fill the roster + male/female shape limits with the researched first-cut numbers. Runs from the
        /// inspector button. Overwrites the shape lists; leaves slots/palettes/base assets for you to fill.</summary>
        public void PopulateFromResearch()
        {
            roster = new List<BaseChoice>
            {
                new BaseChoice { id = "Base/1DefaultMale",   displayName = "Default Male",   gender = Gender.Male },
                new BaseChoice { id = "Base/Galvon",         displayName = "Galvon",         gender = Gender.Male },
                new BaseChoice { id = "Base/Javi",           displayName = "Javi",           gender = Gender.Male },
                new BaseChoice { id = "Base/John",           displayName = "John",           gender = Gender.Male },
                new BaseChoice { id = "Base/1DefaultFemale", displayName = "Default Female", gender = Gender.Female },
                new BaseChoice { id = "Base/Mary",           displayName = "Mary",           gender = Gender.Female },
                new BaseChoice { id = "Base/Sally",          displayName = "Sally",          gender = Gender.Female },
                new BaseChoice { id = "Base/Zulle",          displayName = "Zulle",          gender = Gender.Female },
                new BaseChoice { id = "Base/Chloe",          displayName = "Chloe",          gender = Gender.Female },
            };

            male.baseId = "Base/1DefaultMale";
            male.shapes = MaleDefaults();
            female.baseId = "Base/1DefaultFemale";
            female.shapes = FemaleDefaults();
        }

        /// <summary>Fill <see cref="palettes"/> with a curated starter set: human skin tones (Body+Head linked), natural
        /// hair colours (HairFront+HairBack linked), eye colours, and general outfit colours for Top/Bottom. Slots verified
        /// against the base characters' outfits (Body/Head/Eyes/HairFront/HairBack/Top/Bottom). Runs from the inspector.</summary>
        public void PopulatePalettes()
        {
            palettes = new List<ColorPalette>
            {
                // Skin — the Body outfit's Base channel, linked to Head so the face doesn't go two-tone.
                Pal("Skin", "Body", 1, new[] { "Head" },
                    "#FFE0BD", "#F5CBA7", "#F1C27D", "#E0AC69", "#C68642", "#A5673F", "#8D5524", "#6B4423", "#4A2F1B"),
                // Hair — front + back linked so they match.
                Pal("Hair", "HairFront", 1, new[] { "HairBack" },
                    "#1C1C1C", "#3B2417", "#5A3825", "#7B4B2A", "#A67B4A", "#D4B37A", "#E8DCC0",
                    "#7A3520", "#B55A2A", "#9E9E9E", "#E8E8E8", "#3A6EA5", "#D46A9F"),
                // Eyes — iris colour (verify channel if it doesn't take: eyes may use a channel other than 1).
                Pal("Eyes", "Eyes", 1, null,
                    "#4A2F1B", "#6B4423", "#8B5A2B", "#3A6EA5", "#27AE60", "#7A8B99", "#2C3E50"),
                // General outfit colours.
                Pal("Top", "Top", 1, null,
                    "#EDEDED", "#2A2A2A", "#8A8A8A", "#C0392B", "#E67E22", "#F1C40F", "#27AE60", "#16A085",
                    "#2980B9", "#8E44AD", "#E84393", "#7B4B2A"),
                Pal("Bottom", "Bottom", 1, null,
                    "#EDEDED", "#2A2A2A", "#34495E", "#8A8A8A", "#2980B9", "#27AE60", "#7B4B2A", "#C0392B", "#8E44AD"),
            };
        }

        private static ColorPalette Pal(string target, string slot, int channel, string[] linked, params string[] hexes)
        {
            var p = new ColorPalette { target = target, slot = slot, channel = channel, swatches = new List<Color>() };
            if (linked != null) p.linkedSlots = new List<string>(linked);
            foreach (var h in hexes) p.swatches.Add(ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.white);
            return p;
        }

        /// <summary>Fill <see cref="slots"/> with a curated, ORDERED, labelled set of dressing tabs — the meaningful
        /// wearable slots (clothing / hair / accessories / makeup), skipping the base Body/Head/Eyes/Pupil which aren't
        /// "worn". Empty allowedPartIds on each ⇒ all parts of that slot show. Trim / reorder afterwards to taste.</summary>
        public void PopulateStarterSlots()
        {
            const string Face = "Face", Outfit = "Outfit";
            slots = new List<SlotConfig>
            {
                // ---- Face group (head / face area) ----
                Slot("HairFront",    "Hair",       Face),
                Slot("HairBack",     "Hair Back",  Face),
                Slot("Hat",          "Hat",        Face),
                Slot("UpperFace",    "Glasses",    Face),
                Slot("LowerFace",    "Beard",      Face),
                Slot("HeadAcc",      "Head",       Face),
                Slot("FaceDetails",  "Face",       Face),
                Slot("MakeUpEyes",   "Eye Makeup", Face),
                Slot("MakeUpLips",   "Lips",       Face),
                Slot("MakeUpCheeks", "Blush",      Face),
                // ---- Outfit group (clothing) — some tabs merge several slots ----
                Slot("Top",        "Top",       Outfit),
                Slot("Overall",    "Outfit",    Outfit),
                Slot("Bottom",     "Bottom",    Outfit, "Leggings"),      // Bottom tab also holds Leggings
                Slot("UnderLower", "Underwear", Outfit, "UnderUpper"),    // Underwear tab = underwear + bra
                Slot("Feet",       "Shoes",     Outfit),
                Slot("Socks",      "Socks & Gloves", Outfit, "Gloves"),   // one tab for socks + gloves
            };
        }

        private static SlotConfig Slot(string slot, string label, string group = "Outfit", params string[] extraSlots)
            => new SlotConfig
            {
                slot = slot, label = label, group = group,
                extraSlots = new List<string>(extraSlots),
                allowedPartIds = new List<string>(),
            };

        private static ShapeLimit Bs(string key, string label, ShapeCategory cat, float min, float max, float def, bool risk = false, bool locked = false)
            => new ShapeLimit { key = key, label = label, kind = ShapeKind.Blendshape, category = cat, min = min, max = max, def = def, deformRisk = risk, locked = locked };
        private static ShapeLimit Mu(string key, string label, float min, float max, float def, bool risk = false)
            => new ShapeLimit { key = key, label = label, kind = ShapeKind.BoneUniform, category = ShapeCategory.BodyMod, min = min, max = max, def = def, deformRisk = risk };
        private static ShapeLimit Ax(string key, string axis, string label, float min, float max, float def, bool risk = false)
            => new ShapeLimit { key = key, label = label, kind = ShapeKind.BoneAxis, category = ShapeCategory.BodyMod, axis = axis, min = min, max = max, def = def, deformRisk = risk };

        private static List<ShapeLimit> MaleDefaults() => new List<ShapeLimit>
        {
            // body (SetShape 0-100)
            Bs("BodyType", "Body Type (gender)", ShapeCategory.Body, 100, 100, 100, false, true),   // 🔒 gender axis
            Bs("Chest", "Chest", ShapeCategory.Body, 0, 10, 0),
            Bs("Weight", "Weight", ShapeCategory.Body, 0, 80, 0),
            Bs("Belly", "Belly", ShapeCategory.Body, 0, 70, 0),
            Bs("Muscle", "Muscle", ShapeCategory.Body, 0, 100, 0),
            Bs("NeckThickness", "Neck Thickness", ShapeCategory.Body, 60, 100, 100),
            // face (SetShape on the head mesh — same key strings, disambiguated by category=Face)
            Bs("BodyType", "Face Masculinity", ShapeCategory.Face, 40, 100, 50),
            Bs("Weight", "Face Weight", ShapeCategory.Face, 0, 70, 0),
            Bs("Muscle", "Face Muscle", ShapeCategory.Face, 0, 70, 50),
            Bs("Squareness", "Jaw Squareness", ShapeCategory.Face, 0, 100, 0),
            Bs("Sharpness", "Jaw Sharpness", ShapeCategory.Face, 0, 100, 100),
            Bs("LashLength", "Lash Length", ShapeCategory.Face, 0, 60, 0),
            Bs("BrowsThickness", "Brow Thickness", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseBridgeCurve", "Nose Bridge", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseWidth", "Nose Width", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseTiltDown", "Nose Tilt Down", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseTiltUp", "Nose Tilt Up", ShapeCategory.Face, 0, 100, 0),
            Bs("MouthWide", "Mouth Width", ShapeCategory.Face, 0, 100, 0),
            Bs("MouthThin", "Lip Thinness", ShapeCategory.Face, 0, 100, 0),
            Bs("EyesOuterCornersLow", "Eye Corner Down", ShapeCategory.Face, 0, 25, 0, true),
            Bs("EyesOuterCornersHigh", "Eye Corner Up", ShapeCategory.Face, 0, 25, 0, true),
            Bs("EyesSquare", "Eye Squareness", ShapeCategory.Face, 0, 20, 0, true),
            Bs("EarsElf", "Ear Point", ShapeCategory.Face, 0, 20, 0, true),
            Bs("EarAngle", "Ear Angle", ShapeCategory.Face, 0, 45, 0, true),
            // bone-mods (BodyShapeModifier scale ~1.0)
            Mu("root", "Height / Scale", 0.97f, 1.06f, 1.05f),
            Mu("spine_04", "Torso", 0.95f, 1.08f, 1.06f),
            Mu("clavicle_l", "Shoulder Width", 0.97f, 1.12f, 1.009f),
            Mu("shoulder_twist_l", "Shoulders", 1.00f, 1.25f, 1.206f),
            Mu("upperarm_twist_02_l", "Upper Arm", 0.95f, 1.20f, 1.0f),
            Mu("lowerarm_twist_02_l", "Forearm / Biceps", 1.00f, 1.22f, 1.20f),
            Mu("thigh_twist_01_l", "Thigh", 0.90f, 1.15f, 0.922f),
            Mu("calf_twist_01_l", "Calf", 1.00f, 1.25f, 1.212f),
            Mu("head", "Head Size", 0.95f, 1.05f, 0.975f),
            Ax("eyeRoot_l", "x", "Eye Width", 0.88f, 1.00f, 0.934f, true),
            Ax("eyeRoot_l", "y", "Eye Height", 0.85f, 1.00f, 0.949f, true),
        };

        private static List<ShapeLimit> FemaleDefaults() => new List<ShapeLimit>
        {
            Bs("BodyType", "Body Type (gender)", ShapeCategory.Body, 0, 0, 0, false, true),          // 🔒 gender axis
            Bs("Chest", "Chest", ShapeCategory.Body, 30, 100, 75),                                    // floor 30: can't be flat
            Bs("Weight", "Weight", ShapeCategory.Body, 0, 80, 0),
            Bs("Belly", "Belly", ShapeCategory.Body, 0, 60, 0),
            Bs("Muscle", "Muscle", ShapeCategory.Body, 0, 80, 0),
            Bs("NeckThickness", "Neck Thickness", ShapeCategory.Body, 55, 100, 100),
            Bs("BodyType", "Face Femininity", ShapeCategory.Face, 0, 40, 0),
            Bs("Weight", "Face Weight", ShapeCategory.Face, 0, 70, 0),
            Bs("Muscle", "Face Muscle", ShapeCategory.Face, 0, 50, 0),
            Bs("Squareness", "Jaw Squareness", ShapeCategory.Face, 0, 70, 0),
            Bs("Sharpness", "Jaw Sharpness", ShapeCategory.Face, 0, 100, 0),
            Bs("LashLength", "Lash Length", ShapeCategory.Face, 0, 100, 100),
            Bs("BrowsThickness", "Brow Thickness", ShapeCategory.Face, 0, 90, 0),
            Bs("NoseBridgeCurve", "Nose Bridge", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseWidth", "Nose Width", ShapeCategory.Face, 0, 90, 0),
            Bs("NoseTiltDown", "Nose Tilt Down", ShapeCategory.Face, 0, 100, 0),
            Bs("NoseTiltUp", "Nose Tilt Up", ShapeCategory.Face, 0, 100, 0),
            Bs("MouthWide", "Mouth Width", ShapeCategory.Face, 0, 100, 0),
            Bs("MouthThin", "Lip Thinness", ShapeCategory.Face, 0, 100, 0),
            Bs("EyesOuterCornersLow", "Eye Corner Down", ShapeCategory.Face, 0, 25, 0, true),
            Bs("EyesOuterCornersHigh", "Eye Corner Up", ShapeCategory.Face, 0, 30, 0, true),
            Bs("EyesSquare", "Eye Squareness", ShapeCategory.Face, 0, 20, 0, true),
            Bs("EarsElf", "Ear Point", ShapeCategory.Face, 0, 20, 0, true),
            Bs("EarAngle", "Ear Angle", ShapeCategory.Face, 0, 45, 0, true),
            Mu("root", "Height / Scale", 0.96f, 1.04f, 1.0f),
            Mu("spine_04", "Torso", 0.90f, 1.03f, 0.955f),
            Mu("clavicle_l", "Shoulder Width", 0.95f, 1.02f, 0.978f),
            Mu("shoulder_twist_l", "Shoulders", 0.80f, 1.08f, 1.0f),
            Mu("upperarm_twist_02_l", "Upper Arm", 0.95f, 1.22f, 1.0f),
            Mu("lowerarm_twist_02_l", "Forearm", 1.00f, 1.20f, 1.0f),
            Mu("thigh_twist_01_l", "Thigh", 1.00f, 1.15f, 1.103f),
            Mu("calf_twist_01_l", "Calf", 1.00f, 1.25f, 1.219f),
            Mu("head", "Head Size", 0.96f, 1.04f, 1.0f),
            Ax("eyeRoot_l", "x", "Eye Width", 0.95f, 1.05f, 1.0f, true),
            Ax("eyeRoot_l", "y", "Eye Height", 0.90f, 1.12f, 1.0f, true),
        };
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(AvatarConfig))]
    public sealed class AvatarConfigEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var cfg = (AvatarConfig)target;
            EditorGUILayout.HelpBox(
                "Player avatar limits over BoZo. Click below to fill the roster + Male/Female shape limits with the " +
                "researched first-cut numbers (mined from BoZo's premade humans). Then tune the ⚙️ open-range params " +
                "against the real mesh, and fill Slots + Palettes.", MessageType.Info);
            if (GUILayout.Button("Populate limits from research (Male + Female)"))
            {
                Undo.RecordObject(cfg, "Populate avatar limits");
                cfg.PopulateFromResearch();
                EditorUtility.SetDirty(cfg);
            }
            if (GUILayout.Button("Populate starter palettes (skin / hair / eyes / top / bottom)"))
            {
                Undo.RecordObject(cfg, "Populate avatar palettes");
                cfg.PopulatePalettes();
                EditorUtility.SetDirty(cfg);
            }
            if (GUILayout.Button("Populate starter slots (ordered + labelled dressing tabs)"))
            {
                Undo.RecordObject(cfg, "Populate avatar slots");
                cfg.PopulateStarterSlots();
                EditorUtility.SetDirty(cfg);
            }
            EditorGUILayout.Space();
            DrawDefaultInspector();
        }
    }
#endif
}

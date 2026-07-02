using System.Collections.Generic;
using System.Linq;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Bridges our compact, server-synced <see cref="AvatarData"/> and BoZo's runtime <see cref="CharacterData"/>.
    ///
    /// <b>Display path</b> (<see cref="ToCharacter"/>): clone the chosen premade base, then OVERLAY the player's saved
    /// shapes / body-mods / outfits on top. Keys the player never touched keep the base's value, so a saved avatar is a
    /// small diff over a base, not a full dump.
    ///
    /// <b>Save path</b> (<see cref="FromCharacter"/>): snapshot the live rig's full state back into an AvatarData. Gender
    /// and base id aren't in CharacterData, so the creator supplies them.
    /// </summary>
    public static class AvatarMapper
    {
        // ---- AvatarData → CharacterData (base + overlay) ----

        public static CharacterData ToCharacter(AvatarData a, CharacterData baseData)
        {
            baseData ??= new CharacterData();
            EnsureLists(baseData);                    // guard the SOURCE first — BoZo's copy ctor does new List<T>(x) and throws on null
            var data = new CharacterData(baseData);   // copy (fresh lists) — safe to overlay/mutate
            if (a == null) return data;

            if (a.Body != null && a.Body.Count > 0) MergeShapes(data.bodyIDs, data.bodyShapes, a.Body);
            if (a.Face != null && a.Face.Count > 0) MergeShapes(data.faceIDs, data.faceShapes, a.Face);
            if (a.Mods != null && a.Mods.Count > 0) MergeMods(data, a.Mods);
            // Outfits are a complete set when saved (creator snapshots them all), so replace wholesale when present.
            if (a.Outfits != null && a.Outfits.Count > 0)
                data.outfitDatas = a.Outfits.Where(o => o != null && !string.IsNullOrEmpty(o.Path)).Select(ToOutfitData).ToList();

            return data;
        }

        // ---- CharacterData → AvatarData (snapshot) ----

        public static AvatarData FromCharacter(CharacterData d, string gender, string baseId)
        {
            var a = new AvatarData { Gender = gender, BaseId = baseId };
            if (d == null) return a;

            a.Body = Zip(d.bodyIDs, d.bodyShapes);
            a.Face = Zip(d.faceIDs, d.faceShapes);

            a.Mods = new List<AvatarModData>();
            if (d.bodyModsKeys != null && d.bodyMods != null)
                for (int i = 0; i < d.bodyModsKeys.Count && i < d.bodyMods.Count; i++)
                    a.Mods.Add(ToModData(d.bodyModsKeys[i], d.bodyMods[i]));

            a.Outfits = (d.outfitDatas ?? new List<OutfitData>())
                .Where(o => !string.IsNullOrEmpty(o.outfit)).Select(FromOutfitData).ToList();
            return a;
        }

        // ---- shape/mod helpers ----

        private static void MergeShapes(List<string> ids, List<float> vals, List<AvatarShapeData> src)
        {
            foreach (var s in src)
            {
                if (s == null || string.IsNullOrEmpty(s.Key)) continue;
                int idx = ids.IndexOf(s.Key);
                if (idx >= 0) vals[idx] = s.Value;
                else { ids.Add(s.Key); vals.Add(s.Value); }
            }
        }

        private static void MergeMods(CharacterData data, List<AvatarModData> src)
        {
            foreach (var m in src)
            {
                if (m == null || string.IsNullOrEmpty(m.Bone)) continue;
                var bm = ToBodyModData(m);
                int idx = data.bodyModsKeys.IndexOf(m.Bone);
                if (idx >= 0) data.bodyMods[idx] = bm;
                else { data.bodyModsKeys.Add(m.Bone); data.bodyMods.Add(bm); }
            }
        }

        private static BodyModData ToBodyModData(AvatarModData m) => new BodyModData
        {
            scaleValue = m.Scale,
            scale = new Vector3(m.Sx, m.Sy, m.Sz),
            position = new Vector3(m.Px, m.Py, m.Pz),
            posValue = 0f,
            rotation = 0f,
        };

        private static AvatarModData ToModData(string key, BodyModData bm) => new AvatarModData
        {
            Bone = key,
            Scale = bm.scaleValue,
            Sx = bm.scale.x, Sy = bm.scale.y, Sz = bm.scale.z,
            Px = bm.position.x, Py = bm.position.y, Pz = bm.position.z,
        };

        private static List<AvatarShapeData> Zip(List<string> ids, List<float> vals)
        {
            var list = new List<AvatarShapeData>();
            if (ids == null || vals == null) return list;
            for (int i = 0; i < ids.Count && i < vals.Count; i++)
                list.Add(new AvatarShapeData { Key = ids[i], Value = vals[i] });
            return list;
        }

        // ---- outfit + colour helpers ----

        private static OutfitData ToOutfitData(AvatarOutfitData o) => new OutfitData
        {
            outfit = o.Path,
            colors = (o.Colors ?? new List<string>()).Select(ParseColor).ToList(),
            // Safe defaults so BoZo's LoadCharacter never dereferences null pattern data.
            pattern = "",
            patternColors = new List<Color>(),
            patternScale = Vector4.one,
        };

        private static AvatarOutfitData FromOutfitData(OutfitData od) => new AvatarOutfitData
        {
            Path = od.outfit,
            Colors = (od.colors ?? new List<Color>()).Select(HexOf).ToList(),
        };

        internal static void EnsureLists(CharacterData d)
        {
            d.bodyIDs ??= new List<string>();
            d.bodyShapes ??= new List<float>();
            d.faceIDs ??= new List<string>();
            d.faceShapes ??= new List<float>();
            d.bodyModsKeys ??= new List<string>();
            d.bodyMods ??= new List<BodyModData>();
            d.outfitDatas ??= new List<OutfitData>();
        }

        /// <summary>"#RRGGBB" (or "RRGGBB") → Color; white on parse failure.</summary>
        public static Color ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return Color.white;
            if (s[0] != '#') s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out var c) ? c : Color.white;
        }

        /// <summary>Color → "#RRGGBB" (alpha dropped; avatars don't need it).</summary>
        public static string HexOf(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}

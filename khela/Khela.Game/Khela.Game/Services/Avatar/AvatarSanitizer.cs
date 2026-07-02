using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Avatar;

namespace Khela.Game.Services.Avatar
{
    /// <summary>
    /// Server-side gate for a player-submitted avatar: clamps every value into a HUMAN-plausible band and caps
    /// counts/lengths, so a hacked client can't persist an inhuman/griefing avatar. This is the anti-abuse floor —
    /// generous global bounds. (The exact per-parameter limits — ears ≤ 20, eye-axis clamps, etc. — are the client's
    /// AvatarConfig for UX; enforcing those precise ranges here later just needs the shared limit table.)
    /// </summary>
    public static class AvatarSanitizer
    {
        // Global anti-abuse bounds (deliberately wider than the client UX limits; the point is to stop extremes).
        private const float ShapeMin = 0f, ShapeMax = 100f;
        private const float ModScaleMin = 0.5f, ModScaleMax = 1.6f;   // no reptile/giant limbs
        private const float ModPosAbs = 0.05f;                        // tiny bone nudges only
        private const int MaxShapes = 64, MaxMods = 48, MaxOutfits = 24, MaxColors = 12;
        private const int KeyLen = 48, PathLen = 96, ColorLen = 16, BaseLen = 128;

        public static AvatarDto Sanitize(AvatarDto a)
        {
            if (a == null) return null;
            a.Gender = string.Equals((a.Gender ?? "").Trim(), "Female", StringComparison.OrdinalIgnoreCase) ? "Female" : "Male";
            a.BaseId = Clip(a.BaseId, BaseLen);
            a.Body = Shapes(a.Body);
            a.Face = Shapes(a.Face);
            a.Mods = Mods(a.Mods);
            a.Outfits = Outfits(a.Outfits);
            return a;
        }

        private static List<AvatarShapeDto> Shapes(List<AvatarShapeDto> src) =>
            (src ?? new List<AvatarShapeDto>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Key))
                .Take(MaxShapes)
                .Select(s => new AvatarShapeDto { Key = Clip(s.Key, KeyLen), Value = Clamp(s.Value, ShapeMin, ShapeMax) })
                .ToList();

        private static List<AvatarModDto> Mods(List<AvatarModDto> src) =>
            (src ?? new List<AvatarModDto>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Bone))
                .Take(MaxMods)
                .Select(m => new AvatarModDto
                {
                    Bone = Clip(m.Bone, KeyLen),
                    Scale = Clamp(m.Scale, ModScaleMin, ModScaleMax),
                    Sx = Clamp(m.Sx, ModScaleMin, ModScaleMax),
                    Sy = Clamp(m.Sy, ModScaleMin, ModScaleMax),
                    Sz = Clamp(m.Sz, ModScaleMin, ModScaleMax),
                    Px = Clamp(m.Px, -ModPosAbs, ModPosAbs),
                    Py = Clamp(m.Py, -ModPosAbs, ModPosAbs),
                    Pz = Clamp(m.Pz, -ModPosAbs, ModPosAbs),
                })
                .ToList();

        private static List<AvatarOutfitDto> Outfits(List<AvatarOutfitDto> src) =>
            (src ?? new List<AvatarOutfitDto>())
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Path))
                .Take(MaxOutfits)
                .Select(o => new AvatarOutfitDto
                {
                    Path = Clip(o.Path, PathLen),
                    Colors = (o.Colors ?? new List<string>()).Where(c => c != null).Take(MaxColors).Select(c => Clip(c, ColorLen)).ToList(),
                })
                .ToList();

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
        private static string Clip(string s, int len) => string.IsNullOrEmpty(s) ? s : (s.Length > len ? s.Substring(0, len) : s);
    }
}

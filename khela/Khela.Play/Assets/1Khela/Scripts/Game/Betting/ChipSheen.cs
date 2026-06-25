using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Layers the additive <c>Khela/ChipSheen</c> glint OVERLAY material onto a single spawned chip's renderer — a
    /// SECOND material drawn over the chip's normal material, so only the chips a spawner chooses shimmer and the
    /// shared base material (URP/Lit, metallic, texture) is never touched or instanced. Each chip's glint phase is
    /// offset by its world position in the shader, so a row/stack shimmers on its own beat.
    /// </summary>
    public static class ChipSheen
    {
        private const string ShaderName = "Khela/ChipSheen";   // the glint overlay's shader, used to detect it for removal

        /// <summary>Add the glint overlay to this chip's main renderer. Call right after spawning; no-op if null.</summary>
        public static void Apply(GameObject chip, Material overlay)
        {
            if (chip == null || overlay == null) return;
            var r = chip.GetComponent<Renderer>();
            if (r == null) r = chip.GetComponentInChildren<MeshRenderer>();
            if (r == null) return;

            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == overlay) return;                 // already layered

            var next = new Material[mats.Length + 1];
            for (int i = 0; i < mats.Length; i++) next[i] = mats[i];
            next[mats.Length] = overlay;
            r.sharedMaterials = next;                           // shared assignment → base material is NOT instanced
        }

        /// <summary>Strip the glint overlay off this chip's main renderer — e.g. once it settles into the bet. No-op if absent.</summary>
        public static void Remove(GameObject chip)
        {
            if (chip == null) return;
            var r = chip.GetComponent<Renderer>();
            if (r == null) r = chip.GetComponentInChildren<MeshRenderer>();
            if (r == null) return;

            var mats = r.sharedMaterials;
            int keep = 0;
            for (int i = 0; i < mats.Length; i++) if (!IsSheen(mats[i])) keep++;
            if (keep == mats.Length) return;                   // no overlay present → nothing to strip

            var next = new Material[keep];
            int j = 0;
            for (int i = 0; i < mats.Length; i++) if (!IsSheen(mats[i])) next[j++] = mats[i];
            r.sharedMaterials = next;
        }

        private static bool IsSheen(Material m) => m != null && m.shader != null && m.shader.name == ShaderName;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Pre-built, MOBILE-SAFE outfit catalogue. Built ONCE at edit time (Khela ▸ Avatar ▸ Build Outfit Index) by scanning
    /// Resources; at runtime the wardrobe reads this — paths + icon sprites only, NO meshes — so it never does
    /// <c>Resources.LoadAll&lt;Outfit&gt;("")</c> (which pulls every character mesh into RAM at once = a phone OOM). The
    /// actual outfit prefab is <c>Resources.Load</c>ed only when the player taps it, one at a time.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Avatar Outfit Index", fileName = "OutfitIndex")]
    public sealed class OutfitIndex : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string path;    // Resources path "slot/name" — the equip arg AND the persisted save key
            public string slot;    // OutfitType name (Top / Bottom / HairFront / …)
            public string label;   // display name
            public Sprite icon;    // thumbnail (light; the mesh is NOT referenced here)
        }

        public List<Entry> entries = new List<Entry>();
    }
}

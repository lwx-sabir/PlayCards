using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Client mirror of the server's avatar contract (<c>Khela.Common.Avatar.AvatarDto</c>). Kept client-local — like
    /// <see cref="WalletBalances"/> — so the Unity assembly doesn't need the shared library. STJ maps the server's
    /// camelCase onto these PascalCase props. The <see cref="PlayCard.Avatar.AvatarMapper"/> converts this to/from BoZo's
    /// <c>CharacterData</c>. The SERVER is the source of truth and SANITIZES on write, so these values are trusted.
    /// </summary>
    public sealed class AvatarData
    {
        public string Gender { get; set; }               // "Male" | "Female"
        public string BaseId { get; set; }               // premade base Resources path, e.g. "Base/Mary"
        public List<AvatarShapeData> Body { get; set; } = new List<AvatarShapeData>();
        public List<AvatarShapeData> Face { get; set; } = new List<AvatarShapeData>();
        public List<AvatarModData> Mods { get; set; } = new List<AvatarModData>();
        public List<AvatarOutfitData> Outfits { get; set; } = new List<AvatarOutfitData>();
    }

    /// <summary>One blendshape weight (0–100). Body vs face are separate (the same key can exist on both meshes).</summary>
    public sealed class AvatarShapeData
    {
        public string Key { get; set; }
        public float Value { get; set; }
    }

    /// <summary>One BoZo BodyShapeModifier: uniform scale + optional per-axis scale + a tiny position offset.</summary>
    public sealed class AvatarModData
    {
        public string Bone { get; set; }
        public float Scale { get; set; } = 1f;
        public float Sx { get; set; } = 1f;
        public float Sy { get; set; } = 1f;
        public float Sz { get; set; } = 1f;
        public float Px { get; set; }
        public float Py { get; set; }
        public float Pz { get; set; }
    }

    /// <summary>One equipped outfit part + its colour channels (hex "#RRGGBB").</summary>
    public sealed class AvatarOutfitData
    {
        public string Path { get; set; }                 // Resources path, e.g. "Top/BSMC_Top_Tee"
        public List<string> Colors { get; set; } = new List<string>();
    }

    /// <summary>The server wraps the avatar as <c>{ "avatar": { ... } }</c> (null when the player has none yet).</summary>
    public sealed class AvatarEnvelope
    {
        public AvatarData Avatar { get; set; }
    }
}

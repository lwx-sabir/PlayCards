using System.Collections.Generic;

namespace Khela.Common.Avatar
{
    /// <summary>One blendshape weight (0–100). Body vs face are separate lists (the same key can exist on both meshes).</summary>
    public sealed class AvatarShapeDto
    {
        public string Key { get; set; }
        public float Value { get; set; }
    }

    /// <summary>One BoZo BodyShapeModifier: a uniform scale + optional per-axis scale + tiny position offset.</summary>
    public sealed class AvatarModDto
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

    /// <summary>One equipped outfit part + its colour channels (hex or "r,g,b").</summary>
    public sealed class AvatarOutfitDto
    {
        public string Path { get; set; }                 // Resources path, e.g. "Top/BSMC_Top_Tee"
        public List<string> Colors { get; set; } = new List<string>();
    }

    /// <summary>
    /// The player's avatar — a compact, engine-agnostic config synced to the server (source of truth) so any client can
    /// render this player's BoZo at their seat. The server SANITIZES it on write (bounds/whitelist) so a hacked client
    /// can't push an inhuman avatar. The client maps this to/from BoZo's CharacterData.
    /// </summary>
    public sealed class AvatarDto
    {
        public string Gender { get; set; }               // "Male" | "Female"
        public string BaseId { get; set; }               // premade base Resources path, e.g. "Base/Mary"
        public List<AvatarShapeDto> Body { get; set; } = new List<AvatarShapeDto>();
        public List<AvatarShapeDto> Face { get; set; } = new List<AvatarShapeDto>();
        public List<AvatarModDto> Mods { get; set; } = new List<AvatarModDto>();
        public List<AvatarOutfitDto> Outfits { get; set; } = new List<AvatarOutfitDto>();
    }
}

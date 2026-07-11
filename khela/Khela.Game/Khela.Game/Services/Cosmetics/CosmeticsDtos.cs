using System.Collections.Generic;

namespace Khela.Game.Services.Cosmetics
{
    // ==== import contract — EXACTLY the Unity Cosmetic Exporter's catalog JSON (camelCase via STJ web defaults) ====

    /// <summary>The whole authored catalog file (khela/catalog/cosmetics.json) — POSTed by an admin to import.</summary>
    public sealed class CatalogImportDto
    {
        public List<SkuImportDto> Skus { get; set; } = new List<SkuImportDto>();
    }

    public sealed class SkuImportDto
    {
        public string Id { get; set; }
        public string Type { get; set; }                  // "item" | "set" | "character"
        public string Name { get; set; }
        public string Description { get; set; }
        public string Gender { get; set; }                // "Male" | "Female" | null/empty = unisex
        public string Slot { get; set; }                  // item
        public string Path { get; set; }                  // item
        public string ColorMode { get; set; }             // item: "fixed" | "palette"
        public List<string> DefaultColors { get; set; }   // item: designed colour per channel
        public List<string> Palette { get; set; }         // item palette mode: the buyer's colour grid
        public List<PieceImportDto> Pieces { get; set; }  // set
        public CharacterImportDto Character { get; set; } // character (AvatarDto shape)
        public decimal Price { get; set; }
        public string PriceCurrency { get; set; }         // "Coins" | "Gems" | "Kash" | "Chips" — NEVER "Tokens"
        public bool IsStarter { get; set; }
        public bool Exclusive { get; set; }
        public bool Enabled { get; set; } = true;
        public int SortOrder { get; set; }
    }

    public sealed class PieceImportDto
    {
        public string Path { get; set; }
        public List<string> Colors { get; set; } = new List<string>();
    }

    /// <summary>Mirror of AvatarDto for import (kept separate so the import contract is explicit + stable).</summary>
    public sealed class CharacterImportDto
    {
        public string Gender { get; set; }
        public string BaseId { get; set; }
        public List<ShapeImportDto> Body { get; set; } = new List<ShapeImportDto>();
        public List<ShapeImportDto> Face { get; set; } = new List<ShapeImportDto>();
        public List<ModImportDto> Mods { get; set; } = new List<ModImportDto>();
        public List<PieceImportDto> Outfits { get; set; } = new List<PieceImportDto>();
    }

    public sealed class ShapeImportDto { public string Key { get; set; } public float Value { get; set; } }

    public sealed class ModImportDto
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

    // ==== responses ====

    public sealed class ImportResultDto
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>One shop entry as the client sees it (catalog + the caller's ownership).</summary>
    public sealed class CatalogSkuDto
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Gender { get; set; }
        public string Slot { get; set; }
        public string Path { get; set; }
        public string ColorMode { get; set; }
        public List<string> DefaultColors { get; set; }
        public List<string> Palette { get; set; }
        public List<PieceImportDto> Pieces { get; set; }
        public CharacterImportDto Character { get; set; }
        public decimal Price { get; set; }
        public string PriceCurrency { get; set; }
        public bool IsStarter { get; set; }
        public bool Exclusive { get; set; }
        public int SortOrder { get; set; }
        public bool Owned { get; set; }
        /// <summary>True when a baked product-shot exists — fetch it from GET /api/shop/cosmetics/{id}/icon.</summary>
        public bool HasIcon { get; set; }
    }

    public sealed class PurchaseResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }          // null when Ok
        public bool AlreadyOwned { get; set; }
        public decimal? Balance { get; set; }      // post-purchase balance in the SKU's currency
    }

    /// <summary>Result of validating an avatar's equipped outfits against the caller's entitlements.</summary>
    public sealed class EquipValidationResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public static EquipValidationResult Pass() => new EquipValidationResult { Ok = true };
        public static EquipValidationResult Fail(string error) => new EquipValidationResult { Ok = false, Error = error };
    }
}

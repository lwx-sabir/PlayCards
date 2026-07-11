using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>Response envelope for GET /api/shop/cosmetics ({ "skus": [...] }).</summary>
    public sealed class CosmeticCatalogEnvelope
    {
        public List<CosmeticItemDto> Skus { get; set; } = new List<CosmeticItemDto>();
    }

    /// <summary>One shop cosmetic as the client sees it — the fields the wardrobe grid needs. Server sends camelCase;
    /// the REST client reads case-insensitively. Extra server fields (description, pieces, character…) are ignored.</summary>
    public sealed class CosmeticItemDto
    {
        public string Id { get; set; }
        public string Type { get; set; }              // "item" | "set" | "character"
        public string Name { get; set; }
        public string Slot { get; set; }              // BoZo OutfitType (Top/Bottom/UnderUpper/…)
        public string Path { get; set; }              // Resources key "Slot/Prefab" — the equip arg
        public string ColorMode { get; set; }         // "fixed" | "palette"
        public List<string> DefaultColors { get; set; } = new List<string>();
        public List<string> Palette { get; set; } = new List<string>();
        public List<CosmeticPieceDto> Pieces { get; set; } = new List<CosmeticPieceDto>();   // set only — the costume's pieces
        public decimal Price { get; set; }
        public string PriceCurrency { get; set; }
        public bool IsStarter { get; set; }
        public bool Exclusive { get; set; }
        public bool Owned { get; set; }
        public bool HasIcon { get; set; }

        /// <summary>Free if it's a starter or costs nothing.</summary>
        public bool IsFree => IsStarter || Price <= 0m;

        /// <summary>Colour options the buyer gets — a Palette item's swatch count, else 1.</summary>
        public int ColorCount => ColorMode == "palette" && Palette != null && Palette.Count > 0 ? Palette.Count : 1;
    }

    /// <summary>One piece of a set — a garment path + its designed colours.</summary>
    public sealed class CosmeticPieceDto
    {
        public string Path { get; set; }
        public List<string> Colors { get; set; } = new List<string>();
    }

    /// <summary>Result of POST /api/shop/cosmetics/{id}/buy.</summary>
    public sealed class CosmeticPurchaseResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool AlreadyOwned { get; set; }
        public decimal? Balance { get; set; }
    }
}

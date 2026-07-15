using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum CosmeticSkuType
    {
        Item = 0,       // one outfit piece (a top, a hat, shoes)
        Set = 1,        // exclusive costume — full or partial group of pieces, one purchase
        Character = 2   // sellable premade avatar (full AvatarDto); grants a whole look
    }

    public enum CosmeticColorMode
    {
        Fixed = 0,      // renders in DefaultColors only — the player can't recolour it
        Palette = 1     // premium: the player may colour ANY channel from the SKU's swatch grid (PaletteJson)
    }

    /// <summary>
    /// One sellable cosmetic — the server-side catalog row (source of truth; the client only renders what the
    /// sanitized avatar echo contains). Authored in Unity's Cosmetic Exporter and imported via the admin endpoint.
    /// Money guardrail: <see cref="PriceCurrency"/> must never be <see cref="CurrencyType.Tokens"/> (enforced on
    /// import AND purchase — the token is never spendable in-game).
    /// </summary>
    [Table("CosmeticSkus")]
    [Index(nameof(Enabled), nameof(Type))]
    public class CosmeticSku
    {
        /// <summary>Stable kebab-case id from the authoring tool (e.g. "top-leather-jacket"). Never reused.</summary>
        [Key]
        [MaxLength(64)]
        public string Id { get; set; }

        [Required]
        public CosmeticSkuType Type { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(500)]
        public string Description { get; set; }

        /// <summary>"Male" | "Female" — or null/empty for unisex. Filters the shop AND gates equips (an avatar of the
        /// other gender can't wear it). Character SKUs derive this from their payload's gender on import.</summary>
        [MaxLength(16)]
        public string Gender { get; set; }

        // ---- Item fields (Type == Item) ----

        /// <summary>Outfit slot (BoZo OutfitType name, e.g. "Top").</summary>
        [MaxLength(32)]
        public string Slot { get; set; }

        /// <summary>BoZo Resources path "Slot/PrefabName" — THE equip key the avatar contract uses.</summary>
        [MaxLength(128)]
        public string Path { get; set; }

        public CosmeticColorMode ColorMode { get; set; } = CosmeticColorMode.Fixed;

        /// <summary>JSON array of hex strings — the designed colour per channel (shop preview + initial equip).</summary>
        public string DefaultColorsJson { get; set; }

        /// <summary>JSON array of hex strings — the swatch grid a Palette item's owner may apply to any channel.</summary>
        public string PaletteJson { get; set; }

        // ---- Set fields (Type == Set) ----

        /// <summary>JSON array of { path, colors[] } — the costume's pieces with their fixed designed colours.</summary>
        public string PiecesJson { get; set; }

        // ---- Character fields (Type == Character) ----

        /// <summary>Full AvatarDto JSON (camelCase) — the sellable premade avatar exactly as authored.</summary>
        public string CharacterJson { get; set; }

        // ---- commerce ----

        [Precision(18, 4)]
        public decimal Price { get; set; }

        /// <summary>Coins/Gems/Kash/Chips only — NEVER Tokens (dual-currency guardrail).</summary>
        [Required]
        public CurrencyType PriceCurrency { get; set; } = CurrencyType.Coins;

        /// <summary>Free starter — every player owns it implicitly (no grant row needed).</summary>
        public bool IsStarter { get; set; }

        public bool IsExclusive { get; set; }

        /// <summary>House-only premade avatar (a table Dealer look). Character SKUs ONLY. Hidden from every
        /// player-facing list (catalog + entitlement) and never purchasable — only admin tools list these, to author
        /// and assign table dealers. Never set true for Item/Set.</summary>
        public bool IsDealer { get; set; }

        /// <summary>Disabled SKUs are hidden from the shop and can't be bought; existing owners keep them.</summary>
        public bool Enabled { get; set; } = true;

        public int SortOrder { get; set; }

        // ---- icon (baked 3D product shot from the exporter; served to clients + web admin) ----

        /// <summary>PNG bytes (≤1MB enforced at upload). Kept in the DB so the catalog + art travel together
        /// (single VPS, backed up with the DB). Catalog/entitlement queries PROJECT past this column.</summary>
        public byte[] IconPng { get; set; }

        public DateTime? IconUpdatedAt { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

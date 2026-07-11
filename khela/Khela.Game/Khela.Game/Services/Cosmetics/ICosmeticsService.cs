using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Avatar;

namespace Khela.Game.Services.Cosmetics
{
    /// <summary>
    /// Server-authoritative cosmetics shop: catalog, inventory (grants), wallet-debited purchases, and the
    /// entitlement check that gates avatar saves (the client only ever renders what the sanitized echo contains —
    /// a hacked client cannot wear anything it doesn't own). See docs/AVATAR_SHOP_SPEC.md.
    /// </summary>
    public interface ICosmeticsService
    {
        /// <summary>The enabled catalog with the caller's ownership flags (starters count as owned).</summary>
        Task<List<CatalogSkuDto>> GetCatalogAsync(Guid userId);

        /// <summary>Admin: upsert the authored catalog JSON (from the Unity Cosmetic Exporter). Rejects Tokens pricing.</summary>
        Task<ImportResultDto> ImportAsync(CatalogImportDto file);

        /// <summary>
        /// Buy a SKU: wallet debit (idempotent on <paramref name="correlationId"/>) + grant (unique per user+sku).
        /// Safe to retry with the same correlationId — never double-charges, never double-grants.
        /// </summary>
        Task<PurchaseResultDto> BuyAsync(Guid userId, string skuId, string correlationId);

        /// <summary>
        /// Entitlement gate for avatar saves: every equipped outfit that is cataloged must be justified by an owned
        /// SKU, and its colours must be legal for that SKU (Fixed = the designed colours; Palette = every channel from
        /// the SKU's grid). Uncataloged paths are free basics. NORMALIZES the dto in place (fills missing colours with
        /// the SKU defaults) — store what this returns Ok for.
        /// </summary>
        Task<EquipValidationResult> ValidateEquipsAsync(Guid userId, AvatarDto avatar);

        /// <summary>The SKU's baked product-shot PNG bytes, or null if none/unknown SKU.</summary>
        Task<(byte[] Png, DateTime? UpdatedAt)> GetIconAsync(string skuId);

        /// <summary>Admin: store/replace a SKU's icon (validated PNG, ≤1MB). Returns an error string or null on success.</summary>
        Task<string> SetIconAsync(string skuId, byte[] png);
    }
}

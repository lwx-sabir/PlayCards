using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Khela.Common.Avatar;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Cosmetics
{
    /// <inheritdoc cref="ICosmeticsService"/>
    public sealed class CosmeticsService : ICosmeticsService
    {
        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly ILogger<CosmeticsService> _log;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public CosmeticsService(AppDbContext db, IWalletService wallet, ILogger<CosmeticsService> log)
        {
            _db = db;
            _wallet = wallet;
            _log = log;
        }

        // ---- catalog ----

        public async Task<List<CatalogSkuDto>> GetCatalogAsync(Guid userId)
        {
            // Projection on purpose: never pull IconPng blobs into a catalog listing.
            var skus = await _db.CosmeticSkus.AsNoTracking()
                .Where(s => s.Enabled)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(s => new
                {
                    s.Id, s.Type, s.Name, s.Description, s.Gender, s.Slot, s.Path, s.ColorMode,
                    s.DefaultColorsJson, s.PaletteJson, s.PiecesJson, s.CharacterJson,
                    s.Price, s.PriceCurrency, s.IsStarter, s.IsExclusive, s.SortOrder,
                    HasIcon = s.IconPng != null,
                })
                .ToListAsync();

            var owned = (await _db.UserCosmetics.AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => c.SkuId)
                .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return skus.Select(s => new CatalogSkuDto
            {
                Id = s.Id,
                Type = s.Type.ToString().ToLowerInvariant(),
                Name = s.Name,
                Description = s.Description,
                Gender = s.Gender,
                Slot = s.Slot,
                Path = s.Path,
                ColorMode = s.ColorMode.ToString().ToLowerInvariant(),
                DefaultColors = FromJson<List<string>>(s.DefaultColorsJson),
                Palette = FromJson<List<string>>(s.PaletteJson),
                Pieces = FromJson<List<PieceImportDto>>(s.PiecesJson),
                Character = FromJson<CharacterImportDto>(s.CharacterJson),
                Price = s.Price,
                PriceCurrency = s.PriceCurrency.ToString(),
                IsStarter = s.IsStarter,
                Exclusive = s.IsExclusive,
                SortOrder = s.SortOrder,
                Owned = s.IsStarter || owned.Contains(s.Id),
                HasIcon = s.HasIcon,
            }).ToList();
        }

        // ---- import (admin) ----

        public async Task<ImportResultDto> ImportAsync(CatalogImportDto file)
        {
            var result = new ImportResultDto();
            if (file?.Skus == null || file.Skus.Count == 0)
            {
                result.Errors.Add("Empty catalog.");
                return result;
            }

            foreach (var dto in file.Skus)
            {
                string err = ValidateImport(dto, out var type, out var colorMode, out var currency);
                if (err != null) { result.Errors.Add($"{dto?.Id ?? "?"}: {err}"); continue; }

                var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == dto.Id);
                bool created = sku == null;
                if (created)
                {
                    sku = new CosmeticSku { Id = dto.Id, CreatedAt = DateTime.UtcNow };
                    _db.CosmeticSkus.Add(sku);
                }

                sku.Type = type;
                sku.Name = dto.Name.Trim();
                sku.Description = dto.Description?.Trim();
                // Character SKUs derive gender from their payload; items/sets use the authored value (empty = unisex).
                sku.Gender = type == CosmeticSkuType.Character
                    ? NormalizeGender(dto.Character?.Gender)
                    : NormalizeGender(dto.Gender);
                // Only the payload that belongs to the type is stored (JsonUtility on the tool side emits empty
                // defaults for the unused blocks — don't persist that noise).
                sku.Slot = type == CosmeticSkuType.Item ? dto.Slot : null;
                sku.Path = type == CosmeticSkuType.Item ? dto.Path : null;
                sku.ColorMode = colorMode;
                sku.DefaultColorsJson = type == CosmeticSkuType.Item ? ToJson(NormalizeHexList(dto.DefaultColors)) : null;
                sku.PaletteJson = type == CosmeticSkuType.Item && colorMode == CosmeticColorMode.Palette
                    ? ToJson(NormalizeHexList(dto.Palette)) : null;
                sku.PiecesJson = type == CosmeticSkuType.Set ? ToJson(NormalizePieces(dto.Pieces)) : null;
                sku.CharacterJson = type == CosmeticSkuType.Character && dto.Character != null
                    ? JsonSerializer.Serialize(dto.Character, Json) : null;
                sku.Price = dto.Price;
                sku.PriceCurrency = currency;
                sku.IsStarter = dto.IsStarter;
                sku.IsExclusive = dto.Exclusive;
                sku.Enabled = dto.Enabled;
                sku.SortOrder = dto.SortOrder;
                sku.UpdatedAt = DateTime.UtcNow;

                if (created) result.Created++; else result.Updated++;
            }

            await _db.SaveChangesAsync();
            _log.LogInformation("[Cosmetics] catalog import: {Created} created, {Updated} updated, {Errors} errors.",
                result.Created, result.Updated, result.Errors.Count);
            return result;
        }

        private static string ValidateImport(SkuImportDto dto, out CosmeticSkuType type, out CosmeticColorMode colorMode, out CurrencyType currency)
        {
            type = CosmeticSkuType.Item;
            colorMode = CosmeticColorMode.Fixed;
            currency = CurrencyType.Coins;

            if (dto == null) return "null entry";
            if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Length > 64) return "missing/too-long id";
            if (string.IsNullOrWhiteSpace(dto.Name)) return "missing name";
            if (!Enum.TryParse(dto.Type, ignoreCase: true, out type)) return $"unknown type '{dto.Type}'";
            if (!string.IsNullOrEmpty(dto.ColorMode) && !Enum.TryParse(dto.ColorMode, ignoreCase: true, out colorMode))
                return $"unknown colorMode '{dto.ColorMode}'";
            if (!string.IsNullOrEmpty(dto.PriceCurrency) && !Enum.TryParse(dto.PriceCurrency, ignoreCase: true, out currency))
                return $"unknown currency '{dto.PriceCurrency}'";

            // GUARDRAIL: the token is never spendable in-game. A catalog row priced in Tokens must never exist.
            if (currency == CurrencyType.Tokens) return "Tokens pricing is forbidden (dual-currency guardrail)";
            if (dto.Price < 0) return "negative price";

            if (!string.IsNullOrWhiteSpace(dto.Gender) && NormalizeGender(dto.Gender) == null)
                return $"unknown gender '{dto.Gender}' (Male, Female, or empty for unisex)";

            switch (type)
            {
                case CosmeticSkuType.Item:
                    if (string.IsNullOrWhiteSpace(dto.Path)) return "item needs a path";
                    if (colorMode == CosmeticColorMode.Palette && (dto.Palette == null || dto.Palette.Count == 0))
                        return "palette item needs at least one swatch";
                    break;
                case CosmeticSkuType.Set:
                    if (dto.Pieces == null || dto.Pieces.Count == 0 || dto.Pieces.Any(p => string.IsNullOrWhiteSpace(p?.Path)))
                        return "set needs pieces with paths";
                    break;
                case CosmeticSkuType.Character:
                    if (dto.Character == null || string.IsNullOrWhiteSpace(dto.Character.BaseId))
                        return "character needs a character payload with a baseId";
                    break;
            }
            return null;
        }

        // ---- purchase (money path: idempotent, wallet-debited, audited) ----

        public async Task<PurchaseResultDto> BuyAsync(Guid userId, string skuId, string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 64)
                return new PurchaseResultDto { Ok = false, Error = "Missing/invalid correlationId." };

            var sku = await _db.CosmeticSkus.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skuId);
            if (sku == null || !sku.Enabled)
                return new PurchaseResultDto { Ok = false, Error = "Unknown item." };
            if (sku.IsStarter)
                return new PurchaseResultDto { Ok = true, AlreadyOwned = true };   // starters are implicitly owned
            if (sku.PriceCurrency == CurrencyType.Tokens)   // belt-and-braces: import already forbids this
                return new PurchaseResultDto { Ok = false, Error = "Item is not purchasable." };

            bool owned = await _db.UserCosmetics.AsNoTracking()
                .AnyAsync(c => c.UserId == userId && c.SkuId == skuId);
            if (owned)
                return new PurchaseResultDto { Ok = true, AlreadyOwned = true };   // retry-friendly: buying twice is a no-op

            // 1) Debit first (idempotent on correlationId — a retry after a crash charges nothing extra).
            if (sku.Price > 0)
            {
                try
                {
                    await _wallet.DebitAsync(userId.ToString(), sku.PriceCurrency, sku.Price,
                        TransactionType.Purchase, correlationId,
                        new WalletContext { ExternalRef = $"cosmetic:{sku.Id}" });
                }
                catch (InsufficientFundsException)
                {
                    return new PurchaseResultDto { Ok = false, Error = $"Not enough {sku.PriceCurrency}." };
                }
            }

            // 2) Grant. Unique (UserId, SkuId) → a concurrent duplicate collapses to "already owned".
            _db.UserCosmetics.Add(new UserCosmetic
            {
                UserId = userId,
                SkuId = sku.Id,
                Source = "purchase",
                CorrelationId = correlationId,
            });
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                _log.LogWarning("[Cosmetics] duplicate grant collapsed for {UserId}/{Sku} ({Corr}).", userId, sku.Id, correlationId);
            }

            decimal balance = await _wallet.GetBalanceAsync(userId.ToString(), sku.PriceCurrency);
            _log.LogInformation("[Cosmetics] {UserId} bought {Sku} for {Price} {Currency} ({Corr}).",
                userId, sku.Id, sku.Price, sku.PriceCurrency, correlationId);
            return new PurchaseResultDto { Ok = true, Balance = balance };
        }

        // ---- entitlement gate for avatar saves ----

        public async Task<EquipValidationResult> ValidateEquipsAsync(Guid userId, AvatarDto avatar)
        {
            if (avatar?.Outfits == null || avatar.Outfits.Count == 0) return EquipValidationResult.Pass();

            // Projection on purpose: never pull IconPng blobs into the equip gate.
            var skus = await _db.CosmeticSkus.AsNoTracking().Where(s => s.Enabled)
                .Select(s => new SkuSlim
                {
                    Id = s.Id, Type = s.Type, Name = s.Name, Gender = s.Gender, Path = s.Path,
                    ColorMode = s.ColorMode, DefaultColorsJson = s.DefaultColorsJson, PaletteJson = s.PaletteJson,
                    PiecesJson = s.PiecesJson, CharacterJson = s.CharacterJson, IsStarter = s.IsStarter,
                })
                .ToListAsync();
            if (skus.Count == 0) return EquipValidationResult.Pass();   // bootstrap: no catalog yet → everything is free

            var ownedIds = (await _db.UserCosmetics.AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => c.SkuId)
                .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Which paths are commerce-controlled at all? Uncataloged paths stay free basics.
            var cataloged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in skus)
            {
                if (s.Type == CosmeticSkuType.Item && !string.IsNullOrEmpty(s.Path)) cataloged.Add(s.Path);
                foreach (var p in FromJson<List<PieceImportDto>>(s.PiecesJson) ?? Empty) cataloged.Add(p.Path);
                var ch = FromJson<CharacterImportDto>(s.CharacterJson);
                if (ch?.Outfits != null) foreach (var p in ch.Outfits) cataloged.Add(p.Path);
            }

            var ownedSkus = skus.Where(s => s.IsStarter || ownedIds.Contains(s.Id)).ToList();

            foreach (var outfit in avatar.Outfits.Where(o => o != null && !string.IsNullOrEmpty(o.Path)))
            {
                if (!cataloged.Contains(outfit.Path)) continue;   // free basic

                string reason = null;
                if (!IsJustified(outfit, avatar.Gender, ownedSkus, ref reason))
                    return EquipValidationResult.Fail(reason ?? $"You don't own '{outfit.Path}'.");
            }
            return EquipValidationResult.Pass();
        }

        /// <summary>Blob-free slice of a CosmeticSku — everything the equip gate needs, never the icon.</summary>
        private sealed class SkuSlim
        {
            public string Id, Name, Gender, Path;
            public CosmeticSkuType Type;
            public CosmeticColorMode ColorMode;
            public string DefaultColorsJson, PaletteJson, PiecesJson, CharacterJson;
            public bool IsStarter;
        }

        /// <summary>True when an owned SKU covers this equip (and its colours are legal for that SKU). Gender-locked
        /// SKUs only justify equips on a matching avatar. Normalizes missing colours to the SKU defaults in place.</summary>
        private static bool IsJustified(AvatarOutfitDto outfit, string avatarGender, List<SkuSlim> ownedSkus, ref string reason)
        {
            bool sawPath = false;
            foreach (var sku in ownedSkus)
            {
                // Gender lock: a "Male"/"Female" SKU can't dress the other gender; empty = unisex.
                if (!string.IsNullOrEmpty(sku.Gender) &&
                    !string.Equals(sku.Gender, avatarGender, StringComparison.OrdinalIgnoreCase))
                {
                    if (PathBelongsTo(sku, outfit.Path)) { sawPath = true; reason = $"'{sku.Name}' is for {sku.Gender} avatars."; }
                    continue;
                }
                switch (sku.Type)
                {
                    case CosmeticSkuType.Item when string.Equals(sku.Path, outfit.Path, StringComparison.OrdinalIgnoreCase):
                    {
                        sawPath = true;
                        var defaults = FromJson<List<string>>(sku.DefaultColorsJson) ?? new List<string>();
                        if (outfit.Colors == null || outfit.Colors.Count == 0)
                        {
                            outfit.Colors = new List<string>(defaults);   // normalize: equip renders as designed
                            return true;
                        }
                        if (sku.ColorMode == CosmeticColorMode.Fixed)
                        {
                            if (HexListEquals(outfit.Colors, defaults)) return true;
                            reason = $"'{sku.Name}' can't be recoloured.";
                            continue;
                        }
                        // Palette: every channel colour must come from the SKU's grid (defaults are implicitly legal).
                        var allowed = (FromJson<List<string>>(sku.PaletteJson) ?? new List<string>())
                            .Concat(defaults).Select(NormalizeHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (outfit.Colors.All(c => allowed.Contains(NormalizeHex(c)))) return true;
                        reason = $"'{sku.Name}': one of the colours isn't in its palette.";
                        continue;
                    }

                    case CosmeticSkuType.Set:
                    {
                        var piece = (FromJson<List<PieceImportDto>>(sku.PiecesJson) ?? Empty)
                            .FirstOrDefault(p => string.Equals(p.Path, outfit.Path, StringComparison.OrdinalIgnoreCase));
                        if (piece == null) break;
                        sawPath = true;
                        if (outfit.Colors == null || outfit.Colors.Count == 0)
                        {
                            outfit.Colors = new List<string>(piece.Colors ?? new List<string>());
                            return true;
                        }
                        if (HexListEquals(outfit.Colors, piece.Colors)) return true;
                        reason = $"'{sku.Name}' pieces can't be recoloured.";
                        break;
                    }

                    case CosmeticSkuType.Character:
                    {
                        var ch = FromJson<CharacterImportDto>(sku.CharacterJson);
                        var piece = ch?.Outfits?.FirstOrDefault(p => string.Equals(p.Path, outfit.Path, StringComparison.OrdinalIgnoreCase));
                        if (piece == null) break;
                        sawPath = true;
                        if (outfit.Colors == null || outfit.Colors.Count == 0)
                        {
                            outfit.Colors = new List<string>(piece.Colors ?? new List<string>());
                            return true;
                        }
                        if (HexListEquals(outfit.Colors, piece.Colors)) return true;
                        reason = $"'{sku.Name}' outfits can't be recoloured.";
                        break;
                    }
                }
            }

            if (!sawPath) reason = $"You don't own '{outfit.Path}'.";
            return false;
        }

        // ---- icons (baked 3D product shots) ----

        public async Task<(byte[] Png, DateTime? UpdatedAt)> GetIconAsync(string skuId)
        {
            var row = await _db.CosmeticSkus.AsNoTracking()
                .Where(s => s.Id == skuId)
                .Select(s => new { s.IconPng, s.IconUpdatedAt })
                .FirstOrDefaultAsync();
            return row == null ? (null, null) : (row.IconPng, row.IconUpdatedAt);
        }

        public async Task<string> SetIconAsync(string skuId, byte[] png)
        {
            if (png == null || png.Length == 0) return "Empty upload.";
            if (png.Length > 2 * 1024 * 1024) return "Icon too large (max 2MB).";
            // PNG magic: 89 50 4E 47 0D 0A 1A 0A — reject anything that isn't actually a PNG.
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
                return "Not a PNG.";

            var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == skuId);
            if (sku == null) return "Unknown SKU.";

            sku.IconPng = png;
            sku.IconUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _log.LogInformation("[Cosmetics] icon stored for {Sku} ({Bytes} bytes).", skuId, png.Length);
            return null;
        }

        // ---- helpers ----

        private static readonly List<PieceImportDto> Empty = new List<PieceImportDto>();

        private static string ToJson<T>(T value) where T : class
            => value == null ? null : JsonSerializer.Serialize(value, Json);

        private static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json, Json); } catch { return null; }
        }

        /// <summary>"male"/"Female" → "Male"/"Female"; anything else (incl. empty) → null = unisex.</summary>
        private static string NormalizeGender(string g)
        {
            if (string.Equals(g?.Trim(), "Male", StringComparison.OrdinalIgnoreCase)) return "Male";
            if (string.Equals(g?.Trim(), "Female", StringComparison.OrdinalIgnoreCase)) return "Female";
            return null;
        }

        /// <summary>Does this SKU reference the given outfit path (item path / set piece / character outfit)?</summary>
        private static bool PathBelongsTo(SkuSlim sku, string path)
        {
            switch (sku.Type)
            {
                case CosmeticSkuType.Item:
                    return string.Equals(sku.Path, path, StringComparison.OrdinalIgnoreCase);
                case CosmeticSkuType.Set:
                    return (FromJson<List<PieceImportDto>>(sku.PiecesJson) ?? Empty)
                        .Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
                case CosmeticSkuType.Character:
                    var ch = FromJson<CharacterImportDto>(sku.CharacterJson);
                    return ch?.Outfits != null && ch.Outfits.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
                default:
                    return false;
            }
        }

        /// <summary>"#RRGGBB" / "rrggbb" → "rrggbb" (comparison form).</summary>
        private static string NormalizeHex(string s) => (s ?? "").TrimStart('#').Trim().ToLowerInvariant();

        private static List<string> NormalizeHexList(List<string> list)
            => list == null || list.Count == 0 ? null : list.Select(s => "#" + NormalizeHex(s)).ToList();

        private static List<PieceImportDto> NormalizePieces(List<PieceImportDto> pieces)
            => pieces == null || pieces.Count == 0
                ? null
                : pieces.Select(p => new PieceImportDto { Path = p.Path, Colors = NormalizeHexList(p.Colors) ?? new List<string>() }).ToList();

        private static bool HexListEquals(List<string> a, List<string> b)
        {
            a ??= new List<string>(); b ??= new List<string>();
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(NormalizeHex(a[i]), NormalizeHex(b[i]), StringComparison.Ordinal)) return false;
            return true;
        }
    }
}

using System.Text.Json;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Cosmetics catalog admin (docs/AVATAR_SHOP_SPEC.md). Reads/edits the shared <c>CosmeticSkus</c> table straight
    /// through <see cref="AppDbContext"/> — the same rows the game shop + entitlement gate read. SKU shape/art comes
    /// from the Unity Cosmetic Exporter (import + baked icons); this page tunes the COMMERCE metadata: name,
    /// description, price, currency, gender, starter/exclusive/enabled, sort order. Money guardrail: a SKU may never be
    /// priced in the tradeable token — enforced on save. Icons are served straight from the DB blob so the page is
    /// self-contained (no dependency on the game server running).
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class CosmeticsController : Controller
    {
        private readonly AppDbContext _db;
        public CosmeticsController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<IActionResult> Index(string q, string type)
        {
            var query = _db.CosmeticSkus.AsNoTracking();

            if (Enum.TryParse<CosmeticSkuType>(type, ignoreCase: true, out var t))
                query = query.Where(s => s.Type == t);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(s => s.Id.Contains(term) || s.Name.Contains(term) || (s.Path != null && s.Path.Contains(term)));
            }

            // Project everything EXCEPT the icon blob (served separately) — a catalog list must never drag PNG bytes.
            var rows = await query
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(s => new
                {
                    s.Id, s.Type, s.Name, s.Description, s.Gender, s.Slot, s.Path, s.ColorMode,
                    s.DefaultColorsJson, s.PaletteJson, s.PiecesJson, s.CharacterJson,
                    s.Price, s.PriceCurrency, s.IsStarter, s.IsExclusive, s.Enabled, s.SortOrder,
                    HasIcon = s.IconPng != null, s.IconUpdatedAt,
                })
                .ToListAsync();

            var cards = rows.Select(s => new CosmeticCard
            {
                Id = s.Id,
                Type = s.Type.ToString(),
                Name = s.Name,
                Description = s.Description,
                Gender = string.IsNullOrEmpty(s.Gender) ? "Unisex" : s.Gender,
                Slot = s.Slot,
                Path = s.Path,
                ColorMode = s.ColorMode.ToString(),
                Price = s.Price,
                Currency = s.PriceCurrency.ToString(),
                IsStarter = s.IsStarter,
                IsExclusive = s.IsExclusive,
                Enabled = s.Enabled,
                SortOrder = s.SortOrder,
                HasIcon = s.HasIcon,
                IconVersion = s.IconUpdatedAt?.Ticks ?? 0,
                ColorCount = CountJsonArray(s.DefaultColorsJson),
                SwatchCount = CountJsonArray(s.PaletteJson),
                PieceCount = CountJsonArray(s.PiecesJson),
                CharacterBase = ExtractBaseId(s.CharacterJson),
            }).ToList();

            var vm = new CosmeticsVm
            {
                Query = q,
                TypeFilter = type,
                Items = cards,
                Total = cards.Count,
                EnabledCount = cards.Count(c => c.Enabled),
                ItemCount = cards.Count(c => c.Type == nameof(CosmeticSkuType.Item)),
                SetCount = cards.Count(c => c.Type == nameof(CosmeticSkuType.Set)),
                CharacterCount = cards.Count(c => c.Type == nameof(CosmeticSkuType.Character)),
                MissingIconCount = cards.Count(c => !c.HasIcon),
                Saved = TempData["Saved"] as string,
                Error = TempData["Error"] as string,
            };
            return View(vm);
        }

        /// <summary>Serve a SKU's baked product-shot straight from the DB blob (cacheable). Self-contained — no call to
        /// the game server. 404 when the SKU has no icon yet. <paramref name="download"/> forces a save-as with a clean
        /// filename so the art can be edited externally and re-uploaded.</summary>
        [HttpGet]
        public async Task<IActionResult> Icon(string id, bool download = false)
        {
            var row = await _db.CosmeticSkus.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => new { s.IconPng, s.IconUpdatedAt })
                .FirstOrDefaultAsync();
            if (row?.IconPng == null) return NotFound();
            Response.Headers.CacheControl = "private, max-age=60";
            if (row.IconUpdatedAt != null) Response.Headers.ETag = $"\"{row.IconUpdatedAt.Value.Ticks}\"";
            return download ? File(row.IconPng, "image/png", $"{id}.png") : File(row.IconPng, "image/png");
        }

        /// <summary>Replace a SKU's icon with an externally edited PNG (≤2MB, PNG magic validated).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(4 * 1024 * 1024)]
        public async Task<IActionResult> UploadIcon(string id, IFormFile icon, string q, string type)
        {
            var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == id);
            if (sku == null) { TempData["Error"] = "SKU not found."; return Back(q, type); }
            if (icon == null || icon.Length == 0) { TempData["Error"] = "Pick a PNG file first."; return Back(q, type); }
            if (icon.Length > 2 * 1024 * 1024) { TempData["Error"] = "Icon too large (max 2MB)."; return Back(q, type); }

            using var ms = new MemoryStream();
            await icon.CopyToAsync(ms);
            var png = ms.ToArray();
            // PNG magic: 89 50 4E 47 — reject anything that isn't actually a PNG (keeps the client render path safe).
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            { TempData["Error"] = "That file isn't a PNG."; return Back(q, type); }

            sku.IconPng = png;
            sku.IconUpdatedAt = DateTime.UtcNow;
            sku.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Saved"] = $"Icon replaced for “{sku.Name}” ({png.Length / 1024} KB).";
            return Back(q, type);
        }

        /// <summary>Edit a SKU's commerce metadata. Never touches the mesh/colours/character payload (that's the
        /// exporter's job). Rejects Tokens pricing (dual-currency guardrail).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(string id, string name, string description, string gender,
            decimal price, string priceCurrency, bool isStarter, bool isExclusive, bool enabled, int sortOrder,
            string q, string type)
        {
            var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == id);
            if (sku == null) { TempData["Error"] = "SKU not found."; return Back(q, type); }

            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Name is required."; return Back(q, type); }
            if (price < 0) { TempData["Error"] = "Price can't be negative."; return Back(q, type); }
            if (!Enum.TryParse<CurrencyType>(priceCurrency, ignoreCase: true, out var cur))
            { TempData["Error"] = $"Unknown currency '{priceCurrency}'."; return Back(q, type); }
            if (cur == CurrencyType.Tokens)
            { TempData["Error"] = "Tokens pricing is forbidden (dual-currency guardrail)."; return Back(q, type); }

            sku.Name = name.Trim();
            sku.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            // Character gender is derived from its payload — don't let the admin desync it here.
            if (sku.Type != CosmeticSkuType.Character) sku.Gender = NormalizeGender(gender);
            sku.Price = price;
            sku.PriceCurrency = cur;
            sku.IsStarter = isStarter;
            sku.IsExclusive = isExclusive;
            sku.Enabled = enabled;
            sku.SortOrder = sortOrder;
            sku.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            TempData["Saved"] = $"Saved “{sku.Name}”.";
            return Back(q, type);
        }

        /// <summary>Fast enable/disable toggle (hide from the shop without deleting; existing owners keep it).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(string id, string q, string type)
        {
            var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == id);
            if (sku == null) { TempData["Error"] = "SKU not found."; return Back(q, type); }
            sku.Enabled = !sku.Enabled;
            sku.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Saved"] = $"“{sku.Name}” {(sku.Enabled ? "enabled" : "disabled")}.";
            return Back(q, type);
        }

        /// <summary>Delete a SKU — but only if NO player owns it (deleting owned gear would orphan inventory). Owned
        /// SKUs must be disabled instead.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id, string q, string type)
        {
            var sku = await _db.CosmeticSkus.FirstOrDefaultAsync(s => s.Id == id);
            if (sku == null) { TempData["Error"] = "SKU not found."; return Back(q, type); }

            bool owned = await _db.UserCosmetics.AnyAsync(c => c.SkuId == id);
            if (owned)
            {
                TempData["Error"] = $"“{sku.Name}” is owned by players — disable it instead of deleting.";
                return Back(q, type);
            }

            _db.CosmeticSkus.Remove(sku);
            await _db.SaveChangesAsync();
            TempData["Saved"] = $"Deleted “{sku.Name}”.";
            return Back(q, type);
        }

        private IActionResult Back(string q, string type) => RedirectToAction(nameof(Index), new { q, type });

        private static string NormalizeGender(string g)
        {
            if (string.Equals(g?.Trim(), "Male", StringComparison.OrdinalIgnoreCase)) return "Male";
            if (string.Equals(g?.Trim(), "Female", StringComparison.OrdinalIgnoreCase)) return "Female";
            return null;   // Unisex
        }

        private static int CountJsonArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            }
            catch { return 0; }
        }

        private static string ExtractBaseId(string characterJson)
        {
            if (string.IsNullOrEmpty(characterJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(characterJson);
                return doc.RootElement.TryGetProperty("baseId", out var b) ? b.GetString() : null;
            }
            catch { return null; }
        }
    }

    public sealed class CosmeticsVm
    {
        public string Query { get; set; }
        public string TypeFilter { get; set; }
        public List<CosmeticCard> Items { get; set; } = new();
        public int Total { get; set; }
        public int EnabledCount { get; set; }
        public int ItemCount { get; set; }
        public int SetCount { get; set; }
        public int CharacterCount { get; set; }
        public int MissingIconCount { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class CosmeticCard
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Gender { get; set; }
        public string Slot { get; set; }
        public string Path { get; set; }
        public string ColorMode { get; set; }
        public decimal Price { get; set; }
        public string Currency { get; set; }
        public bool IsStarter { get; set; }
        public bool IsExclusive { get; set; }
        public bool Enabled { get; set; }
        public int SortOrder { get; set; }
        public bool HasIcon { get; set; }
        public long IconVersion { get; set; }
        public int ColorCount { get; set; }
        public int SwatchCount { get; set; }
        public int PieceCount { get; set; }
        public string CharacterBase { get; set; }

        public bool IsFree => IsStarter || Price <= 0m;

        /// <summary>Short "what's in it" line for the card.</summary>
        public string Summary => Type switch
        {
            nameof(CosmeticSkuType.Item) => ColorMode == nameof(CosmeticColorMode.Palette)
                ? $"{ColorCount} colour ch · {SwatchCount}-swatch palette"
                : $"{ColorCount} colour ch · fixed",
            nameof(CosmeticSkuType.Set) => $"{PieceCount} piece set",
            nameof(CosmeticSkuType.Character) => CharacterBase != null ? $"character · {CharacterBase}" : "character",
            _ => Type,
        };
    }
}

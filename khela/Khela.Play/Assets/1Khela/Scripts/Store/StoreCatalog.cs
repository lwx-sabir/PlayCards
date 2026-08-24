using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using Khela.Common.Store;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Store
{
    /// <summary>
    /// The server's store catalog as THIS platform sees it (GET /api/store/catalog?platform=): our product ids, the
    /// store-product id to buy, what each pays (display only — the server grants from its own copy), per-user
    /// availability. Plain singleton in the <c>PiggyState</c> shape: cached, refreshed on demand, <see cref="Changed"/>
    /// for binders. The last catalog is also persisted to disk so Unity IAP can initialise BEFORE the API answers
    /// (the prices still come from the store; only the product list is cached).
    ///
    /// Nothing here decides anything. What a product pays, whether it may be bought, and what it costs are all server
    /// answers; this only carries them.
    /// </summary>
    public sealed class StoreCatalog
    {
        private static StoreCatalog _instance;
        public static StoreCatalog Instance => _instance ??= new StoreCatalog();

        /// <summary>Fires after every successful refresh (and once when the disk cache is loaded).</summary>
        public event Action<StoreCatalogDto> Changed;

        public StoreCatalogDto Current { get; private set; }
        public bool Loaded => Current != null;
        /// <summary>The store as a whole is on (server kill switch).</summary>
        public bool Enabled => Current != null && Current.Enabled;
        /// <summary>This platform is on (server switch + credentials).</summary>
        public bool PlatformEnabled => Current != null && Current.PlatformEnabled;
        public IReadOnlyList<StoreProductDto> Products => Current?.Products ?? (IReadOnlyList<StoreProductDto>)Array.Empty<StoreProductDto>();
        public IReadOnlyList<StoreSectionDto> Sections => Current?.Sections ?? (IReadOnlyList<StoreSectionDto>)Array.Empty<StoreSectionDto>();

        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static bool IsSignedIn
            => PlayCard.Account.AccountManager.Instance == null   // no auth in this scene: let the call through
            || !string.IsNullOrEmpty(PlayCard.Account.AccountManager.Instance.JwtToken);

        private bool _refreshing;
        private bool _diskLoaded;
        private DateTime _fetchedAtUtc;

        /// <summary>
        /// The server's clock, extrapolated from the last fetch — what sale countdowns tick against. The device clock
        /// decides nothing (the server enforces every window); this only keeps the ribbon honest when the phone's clock is off.
        /// </summary>
        public DateTime ServerNowUtc
            => Current == null || _fetchedAtUtc == DateTime.MinValue ? DateTime.UtcNow : Current.ServerTimeUtc + (DateTime.UtcNow - _fetchedAtUtc);

        private static string CachePath => Path.Combine(Application.persistentDataPath, "store_catalog.json");

        /// <summary>Load the last catalog from disk (once). Returns true if something was loaded. Cheap; safe to call repeatedly.</summary>
        public bool LoadCached()
        {
            if (_diskLoaded) return Current != null;
            _diskLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return false;
                var dto = JsonSerializer.Deserialize<StoreCatalogDto>(File.ReadAllText(CachePath), JsonOpts);
                if (dto?.Products == null || dto.Platform != StorePlatformResolver.Current) return false;   // a cache from another platform's build is useless
                Current = dto;
                _fetchedAtUtc = DateTime.MinValue;   // stale by definition: the next RefreshAsync goes to the server
                Changed?.Invoke(Current);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StoreCatalog] cache unreadable: {ex.Message}");
                return false;
            }
        }

        /// <summary>Fetch the catalog for this platform. Coalesces concurrent calls; honours a 60 s freshness unless forced.</summary>
        public async Task<StoreCatalogDto> RefreshAsync(bool force = false)
        {
            if (_refreshing) return Current;
            if (!IsSignedIn) return Current;
            if (!force && Current != null && DateTime.UtcNow - _fetchedAtUtc < Freshness) return Current;
            _refreshing = true;
            try
            {
                var result = await BlackjackRestClient.Instance.GetStoreCatalogAsync(StorePlatformResolver.Current);
                if (!result.Ok || result.Value == null)
                {
                    Debug.LogWarning($"[StoreCatalog] fetch failed: {result.Error}");
                    return Current;
                }
                Apply(result.Value);
                return Current;
            }
            finally { _refreshing = false; }
        }

        public bool TryGet(string productId, out StoreProductDto product)
        {
            product = null;
            if (string.IsNullOrWhiteSpace(productId) || Current?.Products == null) return false;
            product = Current.Products.FirstOrDefault(p => p != null && string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
            return product != null;
        }

        /// <summary>Reverse lookup: which of our products is sold under a store-product id on this platform.</summary>
        public StoreProductDto ByStoreProductId(string storeProductId)
        {
            if (string.IsNullOrWhiteSpace(storeProductId) || Current?.Products == null) return null;
            return Current.Products.FirstOrDefault(p => p != null && string.Equals(p.StoreProductId, storeProductId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Products of one shop section, in the server's order.</summary>
        public IEnumerable<StoreProductDto> InSection(string sectionKey)
            => Products.Where(p => p != null && string.Equals(p.Section, sectionKey, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.SortOrder);

        /// <summary>The piggy product for (tier, option): the catalog's PiggyBreak product whose effect says so, else the naming
        /// convention <c>piggy_t{tier}_{full|x2|early}</c> the seed catalog uses.</summary>
        public string PiggyProductId(int tier, PiggyBreakOption option)
        {
            var arg = option.ToString();
            var tierText = Math.Max(1, tier).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hit = Products.FirstOrDefault(p => p?.Effect != null
                && string.Equals(p.Effect.Type, "PiggyBreak", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Effect.Arg, arg, StringComparison.OrdinalIgnoreCase)
                && p.Effect.Params != null && p.Effect.Params.TryGetValue("tier", out var t) && t == tierText);
            if (hit != null) return hit.Id;
            string suffix = option == PiggyBreakOption.FullDouble ? "x2" : option == PiggyBreakOption.Early ? "early" : "full";
            return $"piggy_t{tierText}_{suffix}";
        }

        /// <summary>The golden pass product (effect GoldenPass), else the seed id <c>golden_pass</c>.</summary>
        public string GoldenPassProductId
        {
            get
            {
                var hit = Products.FirstOrDefault(p => p?.Effect != null && string.Equals(p.Effect.Type, "GoldenPass", StringComparison.OrdinalIgnoreCase));
                return hit?.Id ?? "golden_pass";
            }
        }

        /// <summary>Total of the currency lines of a product for one currency ("Chips", "Kash") — what the card shows as the amount.</summary>
        public static decimal AmountOf(StoreProductDto product, string currency)
        {
            if (product?.Lines == null) return 0m;
            return product.Lines.Where(l => l != null && l.Kind == (int)Khela.Common.Rewards.RewardKind.Currency && string.Equals(l.Id, currency, StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
        }

        /// <summary>Where the last catalog's <c>ImagesVersion</c> is remembered between sessions.</summary>
        private const string ImagesVersionKey = "store.imagesVersion";

        /// <summary>
        /// Art is cached on disk across sessions, so the SERVER says when it is stale: the catalog carries an images
        /// version that moves only when a product's image urls change, and a move drops the cache.
        ///
        /// The first catalog a device ever sees is not a change — there is nothing cached to throw away — so it only
        /// records the number. Otherwise every fresh install would clear an empty cache and re-download on the spot.
        /// </summary>
        private static void HonourImagesVersion(StoreCatalogDto dto)
        {
            if (dto == null) return;
            bool known = PlayerPrefs.HasKey(ImagesVersionKey);
            int last = PlayerPrefs.GetInt(ImagesVersionKey, 0);
            if (known && last != dto.ImagesVersion)
            {
                PlayCard.Core.RemoteImage.ClearDisk();
                Debug.Log($"[StoreCatalog] images version {last} → {dto.ImagesVersion}: cached art dropped.");
            }
            if (!known || last != dto.ImagesVersion)
            {
                PlayerPrefs.SetInt(ImagesVersionKey, dto.ImagesVersion);
                PlayerPrefs.Save();
            }
        }

        private void Apply(StoreCatalogDto dto)
        {
            // Before Changed fires, so the binders that repaint on it fetch the NEW art rather than the dropped copy.
            HonourImagesVersion(dto);
            Current = dto;
            _fetchedAtUtc = DateTime.UtcNow;
            try { File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, JsonOpts)); }
            catch (Exception ex) { Debug.LogWarning($"[StoreCatalog] cache write failed: {ex.Message}"); }
            Changed?.Invoke(Current);
        }
    }
}

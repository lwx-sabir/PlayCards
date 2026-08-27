using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Store
{
    /// <summary>
    /// Which shop host a player sees.
    ///
    /// The artwork lives in object storage under <see cref="Prefix"/> rather than in the client build — a new host is
    /// an upload, not a release. Two conventions do all the work:
    ///
    /// <list type="bullet">
    /// <item>a file counts as a shop host only if its name ends in <see cref="ShopSuffix"/>. The folder holds other
    /// artwork too, and a marker in the name is what lets both live there without a second listing or a manifest to
    /// keep in step.</item>
    /// <item>sorted order is the ladder: the first <see cref="BeginnerCount"/> are the ones a new player meets, the
    /// rest are for everyone past <see cref="BeginnerMaxLevel"/>. Position rather than a hardcoded 01–04, so renaming
    /// or inserting a file does not silently move someone into the wrong set.</item>
    /// </list>
    ///
    /// LEVEL IS DECIDED HERE, never sent by the client — the client is told only the set it may draw from. Which
    /// picture greets someone is cosmetic, but "the client says what level it is" is a habit that ends up load-bearing
    /// somewhere it matters.
    ///
    /// The listing is cached in process: it changes when someone uploads, not per request, and a storage LIST on every
    /// shop open would be a network round trip charged per call for an answer that is the same all day.
    /// </summary>
    public sealed class ShopGirlDirectory
    {
        public const string Prefix = "shop-images/girls/";
        public const string ShopSuffix = "-shop";
        public const int BeginnerCount = 4;
        /// <summary>Levels up to and including this get the beginner set — i.e. "under 5".</summary>
        public const int BeginnerMaxLevel = 4;

        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

        private readonly IObjectStorage _storage;
        private readonly ILogger<ShopGirlDirectory> _log;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private List<string> _urls = new List<string>();
        private DateTime _listedAtUtc = DateTime.MinValue;

        public ShopGirlDirectory(IObjectStorage storage, ILogger<ShopGirlDirectory> log)
        {
            _storage = storage;
            _log = log;
        }

        /// <summary>The hosts this level may be shown, most-beginner first. Empty only when nothing is uploaded.</summary>
        public async Task<IReadOnlyList<string>> ForLevelAsync(int level, CancellationToken ct = default)
        {
            var all = await AllAsync(ct);
            if (all.Count == 0) return all;

            if (level <= BeginnerMaxLevel) return all.Take(BeginnerCount).ToList();

            var rest = all.Skip(BeginnerCount).ToList();
            // Fewer than BeginnerCount uploaded: an experienced player gets the whole set rather than nothing. A shop
            // with no host is a worse outcome than one whose host is meant for a newer player.
            return rest.Count > 0 ? rest : all;
        }

        /// <summary>Drop the cache — call after an upload so the next request sees it without waiting out the TTL.</summary>
        public void Invalidate() => _listedAtUtc = DateTime.MinValue;

        private async Task<List<string>> AllAsync(CancellationToken ct)
        {
            if (DateTime.UtcNow - _listedAtUtc < CacheFor) return _urls;

            await _gate.WaitAsync(ct);
            try
            {
                if (DateTime.UtcNow - _listedAtUtc < CacheFor) return _urls;   // filled while we queued

                var listed = await _storage.ListAsync(Prefix, max: 500, ct: ct);
                var urls = new List<string>();
                foreach (var o in (listed ?? new List<StoredObject>()).OrderBy(o => o?.Key, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(o?.Key)) continue;
                    if (!IsShopHost(o.Key)) continue;
                    var url = !string.IsNullOrWhiteSpace(o.Url) ? o.Url : _storage.UrlFor(o.Key);
                    if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
                }

                _urls = urls;
                _listedAtUtc = DateTime.UtcNow;
                if (urls.Count == 0)
                    _log.LogWarning("ShopGirlDirectory: nothing under '{Prefix}' ends in '{Suffix}' — the shop will have no host.",
                                    Prefix, ShopSuffix);
                return _urls;
            }
            catch (Exception ex)
            {
                // Serve whatever was last listed. A storage hiccup should cost the shop its freshness, not its host.
                _log.LogWarning(ex, "ShopGirlDirectory: could not list '{Prefix}'; serving {Count} cached.", Prefix, _urls.Count);
                return _urls;
            }
            finally { _gate.Release(); }
        }

        /// <summary>The name — not the path — must end in the marker, before its extension.</summary>
        private static bool IsShopHost(string key)
        {
            var name = key;
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            return name.EndsWith(ShopSuffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}

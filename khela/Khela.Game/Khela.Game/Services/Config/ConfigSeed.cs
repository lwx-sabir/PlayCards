using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Khela.Game.Services.Redis;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Config
{
    /// <summary>
    /// A file of tuning, exported from one environment's admin dashboard and applied to another's Redis on startup.
    ///
    /// It exists because the tuning that matters — the pass ladder, the daily ladder, the piggy's pacing — lives in
    /// Redis, not in source, so it does not travel with a build. Re-authoring it by hand on every environment is both
    /// tedious and the kind of thing that quietly drifts until two servers play differently.
    /// </summary>
    public sealed class ConfigSeedFile
    {
        /// <summary>Format version, so an older server can refuse a file it doesn't understand rather than half-apply it.</summary>
        [JsonPropertyName("khelaConfig")] public int Version { get; set; } = 1;

        [JsonPropertyName("exportedAtUtc")] public DateTime ExportedAtUtc { get; set; }

        /// <summary>Free text — machine, person, or purpose. Logged when applied, so the server can say where its
        /// tuning came from.</summary>
        [JsonPropertyName("note")] public string Note { get; set; }

        /// <summary>Fields of the <c>khela:settings</c> hash: the admin's scalar knobs, keyed by config key.</summary>
        [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = new();

        /// <summary>Whole config documents by Redis key — <c>khela:pass</c>, <c>khela:daily</c> and friends, each a
        /// JSON string exactly as the catalog stores it.</summary>
        [JsonPropertyName("documents")] public Dictionary<string, string> Documents { get; set; } = new();
    }

    /// <summary>
    /// Applies a <see cref="ConfigSeedFile"/> to Redis at startup, ONCE per file content.
    ///
    /// Three rules, each of them the answer to a way this goes wrong:
    ///
    /// 1. <b>Once per content, not once per boot.</b> The applied file's hash is remembered in Redis. Re-deploying the
    ///    same build restarts the server, and re-applying every time would silently undo any tuning done live on that
    ///    environment since — the admin page would work, and its changes would vanish at the next restart.
    ///
    /// 2. <b>Merge, never replace.</b> Only keys present in the file are written; nothing is deleted. A file exported
    ///    with two groups ticked must not wipe the ones that weren't.
    ///
    /// 3. <b>Fail soft.</b> A missing, malformed or unreadable file logs and moves on. Tuning must never be able to
    ///    stop a server from booting.
    /// </summary>
    public static class ConfigSeeder
    {
        /// <summary>Where the last applied file's hash and provenance are recorded.</summary>
        public const string StateKey = "khela:config:seed";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Look for the seed file and apply it if its contents differ from whatever was applied last.
        /// Returns how many entries were written; 0 covers "no file", "already applied" and "nothing in it".
        /// </summary>
        public static async Task<int> ApplyAsync(string path, IRedisService redis, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

            string raw;
            try { raw = await File.ReadAllTextAsync(path); }
            catch (Exception ex) { logger.LogError(ex, "Config seed: could not read {Path}", path); return 0; }

            var hash = Sha256(raw);
            var db = redis.GetDatabase();

            // Already applied? Compared on CONTENT, so an edited file re-applies and an unchanged one never does —
            // which is what makes this safe to leave in place across every future deploy.
            try
            {
                var applied = await db.HashGetAsync(StateKey, "hash");
                if (applied.HasValue && (string)applied == hash)
                {
                    logger.LogInformation("Config seed: {Path} already applied, skipping.", Path.GetFileName(path));
                    return 0;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Config seed: could not read the applied marker; applying anyway.");
            }

            ConfigSeedFile file;
            try
            {
                file = JsonSerializer.Deserialize<ConfigSeedFile>(raw, JsonOpts);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Config seed: {Path} is not valid JSON — ignored.", path);
                return 0;
            }

            if (file == null) return 0;
            if (file.Version > 1)
            {
                logger.LogError("Config seed: {Path} is version {Version}; this server understands 1. Ignored rather " +
                                "than half-applied.", path, file.Version);
                return 0;
            }

            int written = 0;

            if (file.Settings != null && file.Settings.Count > 0)
            {
                var entries = new List<StackExchange.Redis.HashEntry>(file.Settings.Count);
                foreach (var kv in file.Settings)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    entries.Add(new StackExchange.Redis.HashEntry(kv.Key, kv.Value));
                    logger.LogInformation("Config seed: {Key} = {Value}", kv.Key, kv.Value);
                }

                if (entries.Count > 0)
                {
                    await db.HashSetAsync("khela:settings", entries.ToArray());
                    written += entries.Count;
                }
            }

            foreach (var kv in file.Documents ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;

                // Guard the blast radius: this only ever writes khela:* config documents, so a hand-edited file
                // cannot reach a player's wallet cache, a live table, or anything else sharing the instance.
                if (!kv.Key.StartsWith("khela:", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Config seed: refusing document key '{Key}' — only khela:* keys may be seeded.", kv.Key);
                    continue;
                }

                await db.StringSetAsync(kv.Key, kv.Value);
                written++;
                logger.LogInformation("Config seed: wrote document {Key} ({Length} chars)", kv.Key, kv.Value.Length);
            }

            try
            {
                await db.HashSetAsync(StateKey, new[]
                {
                    new StackExchange.Redis.HashEntry("hash", hash),
                    new StackExchange.Redis.HashEntry("appliedAtUtc", DateTime.UtcNow.ToString("u")),
                    new StackExchange.Redis.HashEntry("file", Path.GetFileName(path)),
                    new StackExchange.Redis.HashEntry("exportedAtUtc", file.ExportedAtUtc.ToString("u")),
                    new StackExchange.Redis.HashEntry("note", file.Note ?? string.Empty),
                    new StackExchange.Redis.HashEntry("entries", written.ToString()),
                });
            }
            catch (Exception ex)
            {
                // The tuning landed; only the marker didn't. Worth saying loudly, because the cost is that the next
                // restart applies the same file again — harmless, but it would overwrite live tuning done in between.
                logger.LogError(ex, "Config seed: applied {Count} entries but could not record the marker.", written);
            }

            logger.LogInformation("Config seed: applied {Count} entries from {File} (exported {Exported:u}) {Note}",
                written, Path.GetFileName(path), file.ExportedAtUtc, file.Note ?? "");

            return written;
        }

        private static string Sha256(string value)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
    }
}

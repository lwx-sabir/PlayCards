using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Missions;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Config
{
    /// <summary>One snapshot file on disk.</summary>
    public sealed class ConfigBackupInfo
    {
        public string Key { get; set; }          // "khela:pass"
        public string FileName { get; set; }     // "20260812-041500.json"
        public string FullPath { get; set; }
        public DateTime TakenUtc { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>
    /// Point-in-time copies of the admin-edited config overlays, which otherwise live ONLY in Redis — a cache, not a
    /// backup. Snapshots are plain JSON on disk, written only when the content actually changed, and are NEVER deleted
    /// automatically: old ones are pruned by hand once newer ones look good (they are tiny).
    ///
    /// The admin dashboard reads the same directory to list, download and restore. Both apps must point
    /// <c>Config:BackupDir</c> at the same path when they run on one box.
    /// </summary>
    public interface IConfigBackupService
    {
        /// <summary>The overlay keys under backup.</summary>
        IReadOnlyList<string> Keys { get; }

        /// <summary>Snapshot every key. Returns how many files were written (unchanged configs write nothing).</summary>
        Task<int> BackupAllAsync();

        /// <summary>Snapshot ONE key's current Redis value. Returns the file written, or null if nothing changed.</summary>
        Task<ConfigBackupInfo> BackupAsync(string key);

        /// <summary>Snapshots for a key, newest first.</summary>
        IReadOnlyList<ConfigBackupInfo> List(string key);

        /// <summary>The JSON inside a snapshot; null if the name doesn't resolve inside that key's folder.</summary>
        string Read(string key, string fileName);
    }

    /// <inheritdoc cref="IConfigBackupService"/>
    public sealed class ConfigBackupService : IConfigBackupService
    {
        private static readonly string[] BackedUpKeys =
        {
            PassCatalog.RedisKey,        // khela:pass
            MissionCatalog.RedisKey,     // khela:missions
            ChestCatalog.RedisKey,       // khela:chests
            "khela:settings",            // live runtime settings hash is exported as JSON by the settings page
        };

        private readonly IRedisService _redis;
        private readonly ILogger<ConfigBackupService> _logger;
        private readonly string _root;

        public ConfigBackupService(IRedisService redis, IConfiguration config, IHostEnvironment env, ILogger<ConfigBackupService> logger)
        {
            _redis = redis; _logger = logger;
            var configured = config.GetValue<string>("Config:BackupDir");
            _root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(env.ContentRootPath, "App_Data", "config-backups")
                : configured;
        }

        public IReadOnlyList<string> Keys => BackedUpKeys;

        public async Task<int> BackupAllAsync()
        {
            int written = 0;
            foreach (var key in BackedUpKeys)
            {
                try { if (await BackupAsync(key) != null) written++; }
                catch (Exception ex) { _logger.LogError(ex, "Config backup failed for {Key}", key); }
            }
            return written;
        }

        public async Task<ConfigBackupInfo> BackupAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            string json;
            try
            {
                var value = await _redis.GetDatabase().StringGetAsync(key);
                json = value.HasValue ? value.ToString() : null;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Config backup: Redis unreachable for {Key}", key); return null; }

            // No override stored = nothing to back up; the code defaults ARE the config.
            if (string.IsNullOrWhiteSpace(json)) return null;

            var dir = FolderFor(key);
            Directory.CreateDirectory(dir);

            // Only write when the content actually differs from the newest snapshot, so an untouched config doesn't
            // accumulate a copy every run.
            var newest = List(key).FirstOrDefault();
            if (newest != null && Hash(File.ReadAllText(newest.FullPath)) == Hash(json)) return null;

            var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, json, Encoding.UTF8);
            _logger.LogInformation("Config backup written: {Path} ({Bytes} bytes)", path, json.Length);

            return new ConfigBackupInfo { Key = key, FileName = name, FullPath = path, TakenUtc = DateTime.UtcNow, Bytes = json.Length };
        }

        public IReadOnlyList<ConfigBackupInfo> List(string key)
        {
            var dir = FolderFor(key);
            if (!Directory.Exists(dir)) return Array.Empty<ConfigBackupInfo>();

            return new DirectoryInfo(dir).GetFiles("*.json")
                .OrderByDescending(f => f.Name)
                .Select(f => new ConfigBackupInfo
                {
                    Key = key,
                    FileName = f.Name,
                    FullPath = f.FullName,
                    TakenUtc = f.LastWriteTimeUtc,
                    Bytes = f.Length,
                })
                .ToList();
        }

        public string Read(string key, string fileName)
        {
            // Resolve inside the key's own folder — a name like "..\..\appsettings.json" must not escape it.
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..")) return null;
            var dir = Path.GetFullPath(FolderFor(key));
            var path = Path.GetFullPath(Path.Combine(dir, Path.GetFileName(fileName)));
            if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            return File.ReadAllText(path);
        }

        private string FolderFor(string key) => Path.Combine(_root, key.Replace(':', '_'));

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty)));
        }
    }

    /// <summary>
    /// Runs <see cref="IConfigBackupService.BackupAllAsync"/> at startup and every <c>Config:BackupDays</c> days
    /// (default 3). Deliberately dumb: no retention, no rotation, no pruning — see the service docs.
    /// </summary>
    public sealed class ConfigBackupHostedService : BackgroundService
    {
        private readonly IConfigBackupService _backups;
        private readonly ILogger<ConfigBackupHostedService> _logger;
        private readonly TimeSpan _interval;

        public ConfigBackupHostedService(IConfigBackupService backups, IConfiguration config, ILogger<ConfigBackupHostedService> logger)
        {
            _backups = backups; _logger = logger;
            var days = Math.Clamp(config.GetValue("Config:BackupDays", 3), 1, 90);
            _interval = TimeSpan.FromDays(days);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var written = await _backups.BackupAllAsync();
                    if (written > 0) _logger.LogInformation("Config backup sweep wrote {Count} snapshot(s).", written);
                }
                catch (Exception ex) { _logger.LogError(ex, "Config backup sweep failed"); }

                try { await Task.Delay(_interval, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
        }
    }
}

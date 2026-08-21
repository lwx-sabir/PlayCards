using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// A REAL MySQL database for the pass integration tests — the persistence ordering (reserve → spend → grant →
    /// complete), the unique-index race and the missed-days unlock can only be proven against a real engine.
    ///
    /// Uses its OWN database (<c>Khela_IntegrationTests</c>) built from the current model, so it never touches dev data and
    /// never depends on migration history. Point it elsewhere with the <c>KHELA_TEST_DB</c> environment variable.
    /// </summary>
    public sealed class KhelaDbFixture : IDisposable
    {
        public const string TestDatabase = "Khela_IntegrationTests";
        public string ConnectionString { get; }

        private readonly ServerVersion _serverVersion;

        public KhelaDbFixture()
        {
            ConnectionString = Environment.GetEnvironmentVariable("KHELA_TEST_DB") ?? DeriveFromAppSettings();

            try
            {
                // Detect ONCE: AutoDetect opens a connection, and these tests build a fresh context per simulated request.
                _serverVersion = ServerVersion.AutoDetect(SwapDatabase(ConnectionString, "mysql"));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Pass integration tests need MySQL. Tried '{Scrub(ConnectionString)}'. " +
                    "Start the local MySQL, or set KHELA_TEST_DB to another server. Inner: " + ex.Message, ex);
            }

            using var db = NewContext();
            try
            {
                // Drop and rebuild from the CURRENT model each run. EnsureCreated alone is a no-op on an existing
                // database, so a model change (a new column) would leave the suite running against yesterday's schema
                // and failing with "unknown column". Recreating also stops test rows accumulating forever.
                db.Database.EnsureDeleted();
                db.Database.EnsureCreated();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Pass integration tests need MySQL. Tried '{Scrub(ConnectionString)}'. " +
                    "Start the local MySQL, or set KHELA_TEST_DB to another server. Inner: " + ex.Message, ex);
            }
        }

        public AppDbContext NewContext()
            => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseMySql(ConnectionString, _serverVersion)
                .EnableDetailedErrors()
                .Options);

        /// <summary>A complete pass stack on its own DbContext — call twice to simulate two concurrent requests.</summary>
        public PassStack NewStack()
        {
            var db = NewContext();
            var wallet = new WalletService(db, NullLogger<WalletService>.Instance);

            // ONLY the currency granter is registered: XP and chests would reach ProgressionService/ChestService,
            // which need Redis. An unregistered kind is skipped by design, so the ladder still pays its chips and
            // Kash — which is what the persistence tests are about.
            var grants = new RewardGrantService(
                new IRewardGranter[] { new CurrencyGranter(wallet, NullLogger<CurrencyGranter>.Instance) },
                NullLogger<RewardGrantService>.Instance);

            var rewards = new RewardService(db, wallet, grants, NullLogger<RewardService>.Instance);
            // Ad bypass OFF: these tests assert the REAL catch-up rules, so the switch that hands missed days over
            // free must not be on.
            var rewardOptions = new StaticOptionsMonitor<RewardOptions>(new RewardOptions { BypassAdForMissedDays = false });

            var pass = new PassService(db, grants, rewards, wallet, new NoRedis(), NullLogger<PassService>.Instance, rewardOptions);
            return new PassStack(db, wallet, rewards, pass);
        }

        private static string DeriveFromAppSettings()
        {
            // Keep the credential in ONE place (the app's own settings) instead of copying it into a test file.
            // Walk up from the test binaries looking for the API project's appsettings.json, however deep it sits.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                foreach (var relative in new[] { "appsettings.json", Path.Combine("Khela.Game", "appsettings.json") })
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (!File.Exists(candidate)) continue;
                    var connection = ReadDefaultConnection(candidate);
                    if (!string.IsNullOrWhiteSpace(connection)) return SwapDatabase(connection, TestDatabase);
                }
            }

            throw new InvalidOperationException(
                "Could not find the API's appsettings.json to borrow a MySQL connection string from. " +
                "Set KHELA_TEST_DB to a connection string for the test database.");
        }

        private static string ReadDefaultConnection(string appSettingsPath)
        {
            try
            {
                // appsettings.json here carries // comments (an alternate connection string is parked in one), which
                // strict JSON parsing rejects outright.
                using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                    cs.TryGetProperty("DefaultConnection", out var value))
                    return value.GetString();
            }
            catch { }
            return null;
        }

        private static string SwapDatabase(string connection, string database)
        {
            var parts = connection.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.TrimStart().StartsWith("database=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            parts.Add($"database={database}");
            return string.Join(';', parts) + ";";
        }

        private static string Scrub(string connection)
            => string.Join(';', connection.Split(';').Select(p =>
                p.TrimStart().StartsWith("password=", StringComparison.OrdinalIgnoreCase) ? "password=***" : p));

        public void Dispose() { }
    }

    /// <summary>One request's worth of services, sharing a DbContext.</summary>
    public sealed class PassStack : IDisposable
    {
        public PassStack(AppDbContext db, IWalletService wallet, IRewardService rewards, IPassService pass)
        {
            Db = db; Wallet = wallet; Rewards = rewards; Pass = pass;
        }

        public AppDbContext Db { get; }
        public IWalletService Wallet { get; }
        public IRewardService Rewards { get; }
        public IPassService Pass { get; }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// An IRedisService that has nothing behind it. Deliberate: the pass must keep working with Redis DOWN (the config
    /// overlay read is wrapped in a catch that falls back to <see cref="PassCatalog.Defaults"/>), and these tests prove
    /// it rather than assuming it.
    /// </summary>
    /// <summary>An <see cref="IOptionsMonitor{T}"/> over a fixed value — the production code reads options through a
    /// monitor so a config change lands without a restart, and a test just needs one value that never moves.</summary>
    public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public StaticOptionsMonitor(T value) => _value = value;

        public T CurrentValue => _value;
        public T Get(string name) => _value;
        public IDisposable OnChange(Action<T, string> listener) => null;   // nothing ever changes
    }

    public sealed class NoRedis : IRedisService
    {
        public IDatabase GetDatabase() => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "no redis in tests");
        public IMemoryCache GetMemoryCache() => new MemoryCache(new MemoryCacheOptions());
        public Task<string> GetStringAsync(string key) => Task.FromResult<string>(null);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern) => Task.FromResult(Enumerable.Empty<string>());
    }

    [CollectionDefinition("khela-db")]
    public sealed class KhelaDbCollection : ICollectionFixture<KhelaDbFixture> { }
}

using Microsoft.Extensions.FileProviders;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Dtos;
using Khela.Game.Managers;
using Khela.Game.Managers.SRHubs;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Khela.Game.Services.Stats;
using Khela.Game.Services.Leaderboards;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Presence;
using Khela.Game.Services.Friends;
using Khela.Game.Services.Gifts;
using Khela.Game.Services.Profile;
using Khela.Game.Services.Reports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore; 
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog: reads the "Serilog" section from appsettings (console + rolling daily file at /var/khela/khela.log).
// UseSerilog replaces the default logging providers so every ILogger<T> flows through Serilog.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
 
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
 
//builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
//    .AddEntityFrameworkStores<AppDbContext>()
//    .AddDefaultTokenProviders();

// Add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// Bind JwtSettings
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>()
    ?? throw new InvalidOperationException("Missing 'JwtSettings' configuration section.");
builder.Services.AddSingleton(jwtSettings); 

// Add Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); // Allow local HTTP testing
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtSettings.Issuer,

        ValidateAudience = true,
        ValidAudience = jwtSettings.Audience,

        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),

        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1) // Reduce default 5 min skew
    };

    // WebGL/browser clients cannot attach an Authorization header to the WebSocket/SSE handshake,
    // so the JWT arrives as ?access_token=. Read it from the query string for the SignalR hub paths
    // (native clients still send the header and are unaffected). Required by CLAUDE.md's Networking rule.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            var path = ctx.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/chathub") || path.StartsWithSegments("/blackjackhub")))
            {
                ctx.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// Admin authorization: a real, prod-safe gate. Admin:UserIds (AspNetUsers.Id GUIDs) are admins; Development is
// open for convenience. Replaces the old per-endpoint dev-gates on the reports/reconciliation admin actions.
var adminUserIds = builder.Configuration.GetSection("Admin:UserIds").Get<string[]>() ?? Array.Empty<string>();
var adminDevOpen = builder.Environment.IsDevelopment();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireAssertion(ctx =>
    {
        if (adminDevOpen) return true;   // dev convenience; prod requires the allowlist
        var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return id != null && adminUserIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    }));
});

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Adds the "Authorize" button so JWT-protected endpoints are testable from Swagger.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste your JWT from /api/auth/login or /register (no 'Bearer ' prefix)."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});
// Timeouts tuned for MOBILE on a poor link, not the desktop-shaped defaults.
//
// The defaults are ClientTimeoutInterval 30s / KeepAliveInterval 15s: the server closes a connection it has not
// heard from in 30s, while the client pings every ~15s. That leaves room for exactly one missed ping. On a phone
// where a round-trip is already 1.5-5s and the main thread is busy through the round-end ceremony, two late pings
// is normal — and the socket was being closed mid-round. The player then loses board pushes AND, because the
// app-level heartbeat rides the same hub, gets reaped from the seat 30s later.
//
// 60s to close: survives three missed pings, so a busy frame or a slow burst no longer costs the connection.
// 10s keepalive: the server speaks more often, which also lets the CLIENT notice a dead link sooner, and keeps
// traffic well inside nginx proxy_read_timeout (100s).
// 30s handshake: the default 15s is tight when a single request on this link can take 5s.
builder.Services.AddSignalR(o =>
{
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    o.KeepAliveInterval     = TimeSpan.FromSeconds(10);
    o.HandshakeTimeout      = TimeSpan.FromSeconds(30);
}).AddStackExchangeRedis(
    builder.Environment.IsDevelopment()
        ? builder.Configuration.GetConnectionString("RedisConnectionDevelopment")
        : builder.Configuration.GetConnectionString("RedisConnection"));

// CORS for the Unity WebGL client + cross-origin SignalR (native Android/iOS don't need it).
// Permissive for now (dev); restrict to known origins before production.
builder.Services.AddCors(options =>
{
    options.AddPolicy("KhelaClient", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var redisString = !builder.Environment.IsDevelopment()
    ? builder.Configuration.GetConnectionString("RedisConnection")
    : builder.Configuration.GetConnectionString("RedisConnectionDevelopment");

// Resilient + lazy: AbortOnConnectFail=false so a transient Redis outage doesn't crash startup
// (the multiplexer reconnects in the background); constructed on first resolution, not at boot.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisOptions = ConfigurationOptions.Parse(redisString);
    redisOptions.AbortOnConnectFail = false;
    redisOptions.ConnectRetry = 5;
    redisOptions.ConnectTimeout = 5000;
    return ConnectionMultiplexer.Connect(redisOptions);
});
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<BlackjackTableManager>();
builder.Services.AddHostedService<BlackjackRoundDriver>();   // server round-driver: auto-stand on timeout + auto-settle
builder.Services.AddSingleton<Khela.Game.Games.ThreeCardPoker.ThreeCardPokerTableManager>();   // plug-and-play 3CP module (own manager, reuses shared wallet/redis/audit)
builder.Services.AddHostedService<Khela.Game.Games.ThreeCardPoker.ThreeCardPokerRoundDriver>();   // 3CP round-driver: auto-fold on timeout + auto-settle
builder.Services.AddSingleton<Khela.Game.Games.VideoPoker.VideoPokerService>();   // plug-and-play Video Poker module (single-player REST, reuses shared wallet/redis/audit)
builder.Services.AddHostedService<Khela.Game.Games.VideoPoker.VideoPokerReaper>();   // VP reaper: stand-pat-settle abandoned dealt hands so no bet is stranded
builder.Services.AddHostedService<LeaderboardPruneService>();   // nightly prune of old PlayerDailyStat rows
builder.Services.AddHostedService<Khela.Game.Services.Vip.VipTierReviewService>();   // monthly VIP tier review (gentle decay, §3.4)
builder.Services.AddSingleton<SettlementReconciliationService>();      // one shared instance...
builder.Services.AddHostedService(sp => sp.GetRequiredService<SettlementReconciliationService>());  // ...run as the hosted sweeper (no-op unless Reconciliation:Enabled) AND injectable for the on-demand debug endpoint
builder.Services.AddSingleton<IRedisService , RedisService>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddSingleton<Khela.Game.Services.Auth.IFirebaseTokenVerifier, Khela.Game.Services.Auth.FirebaseTokenVerifier>();   // Firebase-brokered social sign-in (Google/Facebook/Apple/guest)
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IPlayerStatsService, PlayerStatsService>();
// Reward payout seam (docs/PASS_SPEC.md §2): one line-based payload shape + one granter per RewardKind. Handing out a
// NEW kind of thing later (lottery tickets, clothes, another currency) = a RewardKind value + an IRewardGranter here,
// with no change to the systems that award rewards.
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardGranter, Khela.Game.Services.Rewards.CurrencyGranter>();
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardGranter, Khela.Game.Services.Rewards.XpGranter>();
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardGranter, Khela.Game.Services.Rewards.ChestGranter>();
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardGrantService, Khela.Game.Services.Rewards.RewardGrantService>();
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardService, Khela.Game.Services.Rewards.RewardService>();   // claimable reward inbox (level-up enqueues here; paid out on claim)
builder.Services.AddScoped<Khela.Game.Services.Chests.IChestService, Khela.Game.Services.Chests.ChestService>();   // chest system (CK_Chest etc.; opened by the daily-mission bundle)
builder.Services.AddScoped<Khela.Game.Services.Missions.IMissionService, Khela.Game.Services.Missions.MissionService>();   // daily missions (server-authoritative; progress at settle, reward on claim)
builder.Services.AddScoped<Khela.Game.Services.Pass.IPassService, Khela.Game.Services.Pass.PassService>();   // monthly pass: free daily ladder + Golden subscription (docs/PASS_SPEC.md)

// Rewarded-ad catch-up (docs/PASS_SPEC.md §5.6). The verifier is chosen by Ads:Provider and defaults to one that
// REFUSES everything — a deployment that forgets to configure ads grants no credits, rather than granting them to
// anyone who can reach the callback URL.
builder.Services.AddHttpClient();
switch ((builder.Configuration.GetValue<string>("Ads:Provider") ?? "").Trim().ToLowerInvariant())
{
    case "admob":
        builder.Services.AddSingleton<Khela.Game.Services.Ads.IAdSsvVerifier, Khela.Game.Services.Ads.AdMobSsvVerifier>();
        break;
    case "hmac":       // Unity Ads / ironSource style shared-secret callbacks
        builder.Services.AddSingleton<Khela.Game.Services.Ads.IAdSsvVerifier, Khela.Game.Services.Ads.HmacAdSsvVerifier>();
        break;
    default:
        builder.Services.AddSingleton<Khela.Game.Services.Ads.IAdSsvVerifier, Khela.Game.Services.Ads.DisabledAdSsvVerifier>();
        break;
}
builder.Services.AddScoped<Khela.Game.Services.Pass.IPassAdService, Khela.Game.Services.Pass.PassAdService>();

// Daily login reward: a repeating ladder, one free day per calendar day, missed days bought back with verified ads.
// Its own module rather than another pass program — it isn't calendar-bound and has no subscription track — but it
// shares the payout seam, the local-midnight clock and the ad-credit model (docs/DAILY_REWARD_SPEC.md).
builder.Services.AddScoped<Khela.Game.Services.Daily.IDailyService, Khela.Game.Services.Daily.DailyService>();
builder.Services.AddScoped<Khela.Game.Services.Piggy.IPiggyService, Khela.Game.Services.Piggy.PiggyService>();
builder.Services.AddScoped<Khela.Game.Services.Daily.IDailyAdService, Khela.Game.Services.Daily.DailyAdService>();

// One switch, both ladders: Rewards:BypassAdForMissedDays hands missed days over free instead of charging ad views.
// Bound (not captured) so flipping it in appsettings takes effect without a restart.
builder.Services.Configure<Khela.Game.Services.Rewards.RewardOptions>(
    builder.Configuration.GetSection(Khela.Game.Services.Rewards.RewardOptions.Section));

// Config overlays live in Redis, which is a cache, not a backup: snapshot them to disk every few days (and at
// startup), only when the content changed, never deleting anything. The admin dashboard lists/restores them.
// Singleton, not scoped: the hosted sweep below is itself a singleton and would fail scope validation at boot
// otherwise. Its dependencies (Redis, config, environment) are all singletons anyway.
builder.Services.AddSingleton<Khela.Game.Services.Config.IConfigBackupService, Khela.Game.Services.Config.ConfigBackupService>();
builder.Services.AddHostedService<Khela.Game.Services.Config.ConfigBackupHostedService>();
builder.Services.AddScoped<Khela.Game.Services.Progression.IProgressionService, Khela.Game.Services.Progression.ProgressionService>();
builder.Services.AddScoped<Khela.Game.Services.Vip.IVipService, Khela.Game.Services.Vip.VipService>();
builder.Services.AddScoped<Khela.Game.Services.Loyalty.ILoyaltyService, Khela.Game.Services.Loyalty.LoyaltyService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();
if (builder.Configuration.GetValue("Moderation:AiEnabled", false))
    builder.Services.AddSingleton<IChatModerator, AiChatModerator>();   // seam: present, off by default
else
    builder.Services.AddSingleton<IChatModerator, BasicChatModerator>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddSingleton<IPresenceService, PresenceService>();
builder.Services.AddScoped<IFriendsService, FriendsService>();
builder.Services.AddScoped<IGiftService, GiftService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<IReportsService, ReportsService>();
builder.Services.AddScoped<Khela.Game.Services.Cosmetics.ICosmeticsService, Khela.Game.Services.Cosmetics.CosmeticsService>();   // cosmetics shop: catalog/purchases/entitlement gate (docs/AVATAR_SHOP_SPEC.md)
var app = builder.Build();

app.UseSerilogRequestLogging();   // one tidy log line per HTTP request (method, path, status, elapsed)

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("KhelaClient");

// Reward/UI artwork the ADMIN names by url (pass ladder icons, shop art). Read-only by construction — the static
// files middleware answers GET/HEAD only — scoped to this one folder, and public on purpose: the client fetches
// these before it has a token, and there is nothing private in them. Anything not on disk 404s rather than falling
// through to a controller.
var artworkRoot = Path.Combine(app.Environment.ContentRootPath, "filesystem", "Icons");
if (Directory.Exists(artworkRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(artworkRoot),
        RequestPath = "/icons",
        ServeUnknownFileTypes = false,   // images only; an unknown extension is not served at all
        OnPrepareResponse = ctx =>
            ctx.Context.Response.Headers.CacheControl = "public,max-age=604800",   // a week; filenames are stable
    });
}
else
{
    app.Logger.LogWarning("Artwork folder {Path} is missing — reward image urls will 404.", artworkRoot);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
 
app.MapHub<BlackjackHub>("/blackjackhub");
app.MapHub<Khela.Game.Games.ThreeCardPoker.ThreeCardPokerHub>("/threecardhub");
app.MapHub<ChatHub>("/chathub");

// Config seed: tuning exported from another environment's admin dashboard, applied to THIS Redis.
//
// The pass ladder, the daily ladder and the piggy's pacing live in Redis, so none of it travels with a build. Dropping
// the exported file beside the app lets a deploy carry its tuning; applying it once per FILE CONTENT (not once per
// boot) means a later restart never undoes tuning done live on this server since.
using (var cfgScope = app.Services.CreateScope())
{
    try
    {
        var seedPath = builder.Configuration["Config:SeedFile"];
        if (string.IsNullOrWhiteSpace(seedPath)) seedPath = "config/khela-settings.json";
        if (!Path.IsPathRooted(seedPath)) seedPath = Path.Combine(app.Environment.ContentRootPath, seedPath);

        await Khela.Game.Services.Config.ConfigSeeder.ApplyAsync(
            seedPath,
            cfgScope.ServiceProvider.GetRequiredService<Khela.Game.Services.Redis.IRedisService>(),
            app.Logger);
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Config seeding failed at startup."); }
}

// Seed leaderboard definitions + opening season at startup (idempotent; best-effort if the DB is down).
using (var seedScope = app.Services.CreateScope())
{
    try { await seedScope.ServiceProvider.GetRequiredService<ILeaderboardService>().SeedAsync(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Leaderboard seeding failed at startup."); }
}

// Player-ID backfill: every legacy profile gets a permanent, globally-unique public Player ID. One-time and
// idempotent — only fills NULLs, so a no-op once done. The local reserved-set stops in-batch collisions before save.
using (var pidScope = app.Services.CreateScope())
{
    try
    {
        var pidDb = pidScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var missing = await pidDb.UserProfiles.Where(p => p.PublicId == null).ToListAsync();
        if (missing.Count > 0)
        {
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prof in missing)
                prof.PublicId = await Khela.Game.Services.Identity.PublicPlayerId.AllocateAsync(pidDb, reserved);
            await pidDb.SaveChangesAsync();
            app.Logger.LogInformation("Backfilled Player IDs for {Count} legacy profile(s).", missing.Count);
        }
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Player-ID backfill failed at startup."); }
}

app.Run();

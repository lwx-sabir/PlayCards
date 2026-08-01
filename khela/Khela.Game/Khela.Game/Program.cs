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
builder.Services.AddSignalR().AddStackExchangeRedis(
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
builder.Services.AddHostedService<LeaderboardPruneService>();   // nightly prune of old PlayerDailyStat rows
builder.Services.AddHostedService<Khela.Game.Services.Vip.VipTierReviewService>();   // monthly VIP tier review (gentle decay, §3.4)
builder.Services.AddSingleton<SettlementReconciliationService>();      // one shared instance...
builder.Services.AddHostedService(sp => sp.GetRequiredService<SettlementReconciliationService>());  // ...run as the hosted sweeper (no-op unless Reconciliation:Enabled) AND injectable for the on-demand debug endpoint
builder.Services.AddSingleton<IRedisService , RedisService>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddSingleton<Khela.Game.Services.Auth.IFirebaseTokenVerifier, Khela.Game.Services.Auth.FirebaseTokenVerifier>();   // Firebase-brokered social sign-in (Google/Facebook/Apple/guest)
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IPlayerStatsService, PlayerStatsService>();
builder.Services.AddScoped<Khela.Game.Services.Rewards.IRewardService, Khela.Game.Services.Rewards.RewardService>();   // claimable reward inbox (level-up enqueues here; credited on claim)
builder.Services.AddScoped<Khela.Game.Services.Chests.IChestService, Khela.Game.Services.Chests.ChestService>();   // chest system (CK_Chest etc.; opened by the daily-mission bundle)
builder.Services.AddScoped<Khela.Game.Services.Missions.IMissionService, Khela.Game.Services.Missions.MissionService>();   // daily missions (server-authoritative; progress at settle, reward on claim)
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
 
app.MapHub<BlackjackHub>("/blackjackhub");
app.MapHub<Khela.Game.Games.ThreeCardPoker.ThreeCardPokerHub>("/threecardhub");
app.MapHub<ChatHub>("/chathub");

// Seed leaderboard definitions + opening season at startup (idempotent; best-effort if the DB is down).
using (var seedScope = app.Services.CreateScope())
{
    try { await seedScope.ServiceProvider.GetRequiredService<ILeaderboardService>().SeedAsync(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Leaderboard seeding failed at startup."); }
}

app.Run();

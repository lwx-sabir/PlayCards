using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Security.Claims;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog: reads the "Serilog" section from appsettings (console + rolling daily file at /var/khela_Web/khela_web.log).
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Add services to the container. This dashboard serves ONLY its own controllers: Khela.Game is referenced for its
// models/services, but its API controllers must NOT be routed here — they pull game-only services this app doesn't
// register, and a same-named controller (e.g. Cosmetics) collides with the dashboard's. Drop the game controller part.
builder.Services.AddControllersWithViews()
    .ConfigureApplicationPartManager(apm =>
    {
        var gamePart = apm.ApplicationParts
            .FirstOrDefault(p => string.Equals(p.Name, "Khela.Game", StringComparison.OrdinalIgnoreCase));
        if (gamePart != null) apm.ApplicationParts.Remove(gamePart);
    });

// --- Shared Khela MySQL: reuse the game backend's AppDbContext so this dashboard reads/writes the SAME database
//     (profiles, wallets, ledger). Any money/wallet write from here MUST keep the real-money rigor: go through
//     IWalletService (idempotent + locked), never raw SQL. ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// The authoritative, idempotent, row-locked wallet ledger — the ONLY sanctioned path for a chips/coins/gems/kash
// balance change from this dashboard (admin grants). Same implementation the game uses; deps (AppDbContext, logger,
// config) are all registered here. Never write a wallet balance via raw SQL.
builder.Services.AddScoped<IWalletService, WalletService>();

// --- Shared Redis: same instance as the game backend (leaderboards, presence, idempotency keys). ---
var redisString = builder.Environment.IsDevelopment()
    ? builder.Configuration.GetConnectionString("RedisConnectionDevelopment")
    : builder.Configuration.GetConnectionString("RedisConnection");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisString);
    options.AbortOnConnectFail = false;   // resilient: a transient Redis outage won't crash startup
    options.ConnectRetry = 5;
    options.ConnectTimeout = 5000;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRedisService, RedisService>();

// --- Admin auth: cookie-based Identity reusing the game's ApplicationUser store (no JWT — this is a
//     server-rendered dashboard). Login is by game account; the Admin policy decides who actually gets in. ---
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Match Khela.Game's policy so a dashboard registration uses the same rules as a game account.
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
})
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

// Admin gate: authenticated AND (Development is open for convenience, OR the user's AspNetUsers.Id is in the
// Admin:UserIds allowlist — same model as Khela.Game). Set as the FALLBACK policy so EVERY page is admin-only
// unless it opts out with [AllowAnonymous] (only the login/denied pages do).
var adminUserIds = builder.Configuration.GetSection("Admin:UserIds").Get<string[]>() ?? Array.Empty<string>();
var adminDevOpen = builder.Environment.IsDevelopment();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            adminDevOpen ||
            (ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is string id &&
             adminUserIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
    });
    options.FallbackPolicy = options.GetPolicy("Admin");
});

var app = builder.Build();

app.UseSerilogRequestLogging();   // one tidy log line per HTTP request

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();   // attribute-routed API controllers, e.g. GET /api/stats/users

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore; 
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using System.Text;

var builder = WebApplication.CreateBuilder(args); 
 
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
});

builder.Services.AddAuthorization(); 

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
builder.Services.AddSingleton<IRedisService , RedisService>(); 
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IPlayerStatsService, PlayerStatsService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();
builder.Services.AddSingleton<IChatModerator, BasicChatModerator>();
builder.Services.AddScoped<IChatService, ChatService>();
var app = builder.Build();

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
app.MapHub<ChatHub>("/chathub");

// Seed leaderboard definitions + opening season at startup (idempotent; best-effort if the DB is down).
using (var seedScope = app.Services.CreateScope())
{
    try { await seedScope.ServiceProvider.GetRequiredService<ILeaderboardService>().SeedAsync(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Leaderboard seeding failed at startup."); }
}

app.Run();

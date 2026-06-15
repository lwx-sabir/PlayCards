using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Khela.Game.Database
{
    /// <summary>
    /// Design-time factory so `dotnet ef migrations ...` can build AppDbContext WITHOUT running
    /// Program.cs — which eagerly connects to Redis (ConnectionMultiplexer.Connect) and auto-detects
    /// the MySQL server version. Both require live services; neither is needed merely to scaffold a
    /// migration. We pin the MySQL version (ServerVersion.Parse — no connection) so a migration can be
    /// generated offline. Used ONLY by EF tooling; the running app still configures the context in Program.cs.
    /// </summary>
    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .Build();

            var connectionString = config.GetConnectionString("DefaultConnection")
                ?? "server=localhost;port=3306;database=Khela;user=root;password=;";

            // Parse (not AutoDetect) so no MySQL/Redis connection is made at scaffold time.
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseMySql(connectionString, ServerVersion.Parse("8.0.36-mysql"))
                .Options;

            return new AppDbContext(options);
        }
    }
}

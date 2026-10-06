using System.Net;
using Inventory.Infrastructure.Persistence;
using Inventory.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Inventory.IntegrationTests.Persistence;

/// <summary>
/// The container startup path: with Database:MigrateOnStartup the API creates the schema on an empty database
/// and reports ready.
/// </summary>
public class StartupMigrationMySqlTests
{
    [Fact]
    public async Task MigrateOnStartup_CreatesSchemaOnEmptyDatabase_ThenReportsReady()
    {
        var connectionString = new MySqlConnectionStringBuilder(await MySqlTestServer.GetConnectionStringAsync())
        {
            Database = $"inv_boot_{Guid.NewGuid():N}"[..21]
        }.ConnectionString;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, ServerVersion.Parse("8.0.41-mysql")).Options);

        try
        {
            await using var factory = new InventoryApiFactory();
            var api = factory.WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:DefaultConnection", connectionString)
                .UseSetting("Database:MigrateOnStartup", "true"));

            var ready = await api.CreateClient().GetAsync("/health/ready"); // starting the host runs the migration

            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await db.Products.CountAsync());
            Assert.Equal(0, await db.Users.CountAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}

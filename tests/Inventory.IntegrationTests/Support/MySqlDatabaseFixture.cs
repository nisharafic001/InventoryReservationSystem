using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Inventory.IntegrationTests.Support;

/// <summary>
/// Creates a throwaway database on the configured MySQL server, applies migrations, and drops it afterwards.
/// Does nothing when <see cref="MySqlFactAttribute.EnvironmentVariable"/> is not set.
/// </summary>
public sealed class MySqlDatabaseFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString { get; } = string.Empty;

    public MySqlDatabaseFixture()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(MySqlFactAttribute.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
            return;

        ConnectionString = new MySqlConnectionStringBuilder(baseConnectionString)
        {
            // Kept short: Pomelo derives a migration lock name from it, and MySQL caps lock names at 64 chars.
            Database = $"inv_test_{Guid.NewGuid():N}"[..21]
        }.ConnectionString;
    }

    public AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(ConnectionString, ServerVersion.Parse("8.0.41-mysql"))
            .Options);

    /// <summary>The default caller: a regular user, the same identity for every client from this fixture.</summary>
    public User DefaultUser { get; } = TestUsers.Create(UserRole.User, "fixture-user");

    public User AdminUser { get; } = TestUsers.Create(UserRole.Admin, "fixture-admin");

    private WebApplicationFactory<Program> Factory =>
        _factory ??= new InventoryApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString));

    /// <summary>An HTTP client for the real API wired to this fixture's database, authenticated as <see cref="DefaultUser"/>.</summary>
    public HttpClient CreateClient() => Factory.CreateClientFor(DefaultUser);

    /// <summary>An HTTP client authenticated as <paramref name="user"/>.</summary>
    public HttpClient CreateClient(User user) => Factory.CreateClientFor(user);

    public async Task<Product> SeedProductAsync(int totalQuantity)
    {
        var product = Product.Create($"SKU-{Guid.NewGuid():N}"[..20], "Seeded product", totalQuantity, DateTime.UtcNow);

        await using var context = CreateDbContext();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    public async Task<Product> GetProductAsync(Guid id)
    {
        await using var context = CreateDbContext();
        return await context.Products.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    public async Task<List<Reservation>> GetReservationsAsync(Guid productId)
    {
        await using var context = CreateDbContext();
        return await context.Reservations.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync();
    }

    /// <summary>Empties all tables. Tests within one class run sequentially, so this isolates them.</summary>
    public async Task ResetAsync()
    {
        await using var context = CreateDbContext();
        await context.Reservations.ExecuteDeleteAsync();
        await context.Products.ExecuteDeleteAsync();
    }

    public async Task InitializeAsync()
    {
        if (ConnectionString.Length == 0)
            return;

        await using var context = CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        if (ConnectionString.Length == 0)
            return;

        await using var context = CreateDbContext();
        await context.Database.EnsureDeletedAsync();
    }
}

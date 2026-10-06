using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Products;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence.Repositories;
using Inventory.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Inventory.IntegrationTests.Products;

/// <summary>
/// End-to-end tests through the real API, EF Core, and MySQL. Skipped unless INVENTORY_TEST_MYSQL is set.
/// </summary>
public class ProductsMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>
{
    private const string BaseUrl = "/api/v1/products";

    private HttpClient CreateClient() => database.CreateClient(database.AdminUser);

    private static string UniqueSku() => $"SKU-{Guid.NewGuid():N}"[..20];

    [MySqlFact]
    public async Task PostThenGet_RoundTripsThroughMySql()
    {
        using var client = CreateClient();
        var sku = UniqueSku();

        var post = await client.PostAsJsonAsync(BaseUrl, new { sku, name = "iPhone", totalQuantity = 10 });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ProductResponse>();

        var get = await client.GetAsync(post.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created, await get.Content.ReadFromJsonAsync<ProductResponse>());

        await using var db = database.CreateDbContext();
        var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == created!.Id);
        Assert.Equal(sku, row.Sku);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAtUtc.Kind);
    }

    [MySqlFact]
    public async Task Post_DuplicateSku_Returns409()
    {
        using var client = CreateClient();
        var sku = UniqueSku();
        await client.PostAsJsonAsync(BaseUrl, new { sku, name = "First", totalQuantity = 1 });

        var response = await client.PostAsJsonAsync(BaseUrl, new { sku, name = "Second", totalQuantity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [MySqlFact]
    public async Task Repository_AddAsync_DuplicateSkuRace_IsTranslatedByUniqueIndex()
    {
        // Simulates two requests that both passed the SkuExists pre-check.
        var sku = UniqueSku();
        await using (var db = database.CreateDbContext())
            await new ProductRepository(db).AddAsync(Product.Create(sku, "First", 1, DateTime.UtcNow), default);

        await using var db2 = database.CreateDbContext();
        var ex = await Assert.ThrowsAsync<DuplicateSkuException>(
            () => new ProductRepository(db2).AddAsync(Product.Create(sku, "Second", 1, DateTime.UtcNow), default));

        Assert.Equal(sku, ex.Sku);
    }
}

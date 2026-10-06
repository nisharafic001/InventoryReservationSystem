using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Persistence.Repositories;
using Inventory.IntegrationTests.Support;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// End-to-end reservation tests through the real API, EF Core, and MySQL.
/// </summary>
public class ReservationsMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>
{
    private const string BaseUrl = "/api/v1/reservations";

    private Task<HttpResponseMessage> PostReservation(HttpClient client, Guid productId, int quantity) =>
        client.PostAsJsonAsync(BaseUrl, new { productId, quantity });

    [Fact]
    public async Task Post_StockAvailable_Returns201AndHoldsStock()
    {
        var product = await database.SeedProductAsync(totalQuantity: 10);
        using var client = database.CreateClient();

        var response = await PostReservation(client, product.Id, 3);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.ReservationId);
        Assert.Equal(product.Id, body.ProductId);
        Assert.Equal(3, body.Quantity);
        Assert.Equal(ReservationStatus.Active, body.Status);
        Assert.Equal(DateTimeKind.Utc, body.CreatedAtUtc.Kind);
        Assert.Equal(body.CreatedAtUtc.AddMinutes(2), body.ExpiresAtUtc);
        Assert.InRange(body.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(3, stored.ReservedQuantity);
        Assert.Equal(7, stored.AvailableQuantity);

        var reservation = Assert.Single(await database.GetReservationsAsync(product.Id));
        Assert.Equal(body.ReservationId, reservation.Id);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(body.ExpiresAtUtc, reservation.ExpiresAtUtc);
    }

    [Fact]
    public async Task Post_ResponseUsesExpectedContract()
    {
        var product = await database.SeedProductAsync(totalQuantity: 1);
        using var client = database.CreateClient();

        var response = await PostReservation(client, product.Id, 1);

        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["cancelledAtUtc", "confirmedAtUtc", "createdAtUtc", "expiresAtUtc", "productId", "quantity", "reservationId", "status"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Active", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Post_ExactlyRemainingStock_Succeeds_ThenNextFails()
    {
        var product = await database.SeedProductAsync(totalQuantity: 2);
        using var client = database.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await PostReservation(client, product.Id, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostReservation(client, product.Id, 1)).StatusCode);

        Assert.Equal(0, (await database.GetProductAsync(product.Id)).AvailableQuantity);
    }

    [Fact]
    public async Task Post_InsufficientStock_Returns409AndChangesNothing()
    {
        var product = await database.SeedProductAsync(totalQuantity: 2);
        using var client = database.CreateClient();

        var response = await PostReservation(client, product.Id, 3);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(0, stored.ReservedQuantity);
        Assert.Empty(await database.GetReservationsAsync(product.Id));
    }

    [Fact]
    public async Task Post_NonexistentProduct_Returns404()
    {
        using var client = database.CreateClient();
        var productId = Guid.NewGuid();

        var response = await PostReservation(client, productId, 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await database.GetReservationsAsync(productId));
    }

    [Fact]
    public async Task Post_ZeroQuantity_Returns400AndChangesNothing()
    {
        await AssertNonPositiveQuantityRejected(0);
    }

    [Fact]
    public async Task Post_NegativeQuantity_Returns400AndChangesNothing()
    {
        await AssertNonPositiveQuantityRejected(-1);
    }

    private async Task AssertNonPositiveQuantityRejected(int quantity)
    {
        var product = await database.SeedProductAsync(totalQuantity: 5);
        using var client = database.CreateClient();

        var response = await PostReservation(client, product.Id, quantity);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await database.GetProductAsync(product.Id)).ReservedQuantity);
        Assert.Empty(await database.GetReservationsAsync(product.Id));
    }

    [Fact]
    public async Task Post_ConcurrentRequests_NeverOversell()
    {
        const int stock = 5;
        const int requests = 30;
        var product = await database.SeedProductAsync(totalQuantity: stock);
        using var client = database.CreateClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, requests).Select(_ => PostReservation(client, product.Id, 1)));

        Assert.Equal(stock, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(requests - stock, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(stock, stored.ReservedQuantity);
        Assert.Equal(0, stored.AvailableQuantity);
        Assert.Equal(stock, (await database.GetReservationsAsync(product.Id)).Count);
    }

    [Fact]
    public async Task Repository_InsertFails_RollsBackStockUpdate()
    {
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = Reservation.Create(product.Id, "user-1", 2, DateTime.UtcNow);

        await using (var db = database.CreateDbContext())
            await new ReservationRepository(db).ReserveStockAndAddAsync(reservation, default);

        // Re-inserting the same reservation: the stock UPDATE succeeds, then the INSERT hits the primary key.
        await using (var db = database.CreateDbContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new ReservationRepository(db).ReserveStockAndAddAsync(reservation, default));
        }

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(2, stored.ReservedQuantity);
        Assert.Single(await database.GetReservationsAsync(product.Id));
    }
}

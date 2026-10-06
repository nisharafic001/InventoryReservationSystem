using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// Request validation over HTTP, without a database. The repository fails the test if it is ever reached,
/// proving invalid requests are rejected before touching stock.
/// </summary>
public class ReservationsApiTests : IClassFixture<InventoryApiFactory>
{
    private const string BaseUrl = "/api/v1/reservations";
    private readonly HttpClient _client;

    public ReservationsApiTests(InventoryApiFactory factory)
    {
        _client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddScoped<IReservationRepository, UnreachableReservationRepository>()))
            .CreateClientFor(UserRole.User);
    }

    [Theory]
    [InlineData("""{ "productId": "3f2b8f8e-0000-4000-8000-000000000001", "quantity": 0 }""", "quantity")]
    [InlineData("""{ "productId": "3f2b8f8e-0000-4000-8000-000000000001", "quantity": -1 }""", "quantity")]
    [InlineData("""{ "productId": "3f2b8f8e-0000-4000-8000-000000000001" }""", "quantity")]
    [InlineData("""{ "quantity": 1 }""", "productId")]
    [InlineData("""{ "productId": "00000000-0000-0000-0000-000000000000", "quantity": 1 }""", "productId")]
    public async Task Post_InvalidRequest_Returns400WithFieldError(string body, string field)
    {
        var response = await _client.PostAsync(BaseUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"Expected an error for {field}.");
    }

    [Fact]
    public async Task Post_NonGuidProductId_Returns400()
    {
        var response = await _client.PostAsync(
            BaseUrl, new StringContent("""{ "productId": 1, "quantity": 1 }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class UnreachableReservationRepository : IReservationRepository
    {
        public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken) =>
            throw Unreachable();

        public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw Unreachable();

        public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) => throw Unreachable();

        public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) => throw Unreachable();

        public Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken) =>
            throw Unreachable();

        public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw Unreachable();

        private static InvalidOperationException Unreachable() => new("Invalid requests must not reach the repository.");
    }
}

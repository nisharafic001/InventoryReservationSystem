using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests.ErrorHandling;

/// <summary>
/// Every failure path returns the same ProblemDetails shape with a stable errorCode and correlationId,
/// and never leaks internals. Persistence is stubbed so each failure can be triggered deterministically.
/// </summary>
public class ErrorHandlingApiTests : IClassFixture<InventoryApiFactory>
{
    private const string Reservations = "/api/v1/reservations";
    private const string LeakyMessage =
        "SELECT * FROM Users WHERE PasswordHash = 'x'; Server=db;Password=hunter2;User=root";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ScriptedReservationRepository _reservations = new();
    private readonly User _user = TestUsers.Create(UserRole.User);

    public ErrorHandlingApiTests(InventoryApiFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IReservationRepository>(_reservations);
            services.AddSingleton<IProductRepository, InMemoryProductRepository>();
            services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        }));
    }

    private HttpClient UserClient() => _factory.CreateClientFor(_user);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, body.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
        Assert.False(body.TryGetProperty("traceId", out _));
        return body;
    }

    [Fact]
    public async Task Validation_Returns400_ValidationFailed()
    {
        var response = await UserClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 0 });

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("quantity", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400_WithoutParserInternals()
    {
        var response = await UserClient().PostAsync(Reservations, new StringContent("{ \"quantity\": oops", Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("LineNumber", raw);
        Assert.DoesNotContain("BytePosition", raw);
        Assert.DoesNotContain("System.", raw);
    }

    [Fact]
    public async Task MissingToken_Returns401_Unauthorized()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 1 });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task InvalidCredentials_Returns401_InvalidCredentials()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "nobody", password = "x" });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task UserOnAdminEndpoint_Returns403_Forbidden()
    {
        var response = await UserClient().PostAsJsonAsync("/api/v1/products", new { sku = "S-1", name = "n", totalQuantity = 1 });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task OtherUsersReservation_Returns403_Forbidden()
    {
        var reservation = Reservation.Create(Guid.NewGuid(), "someone-else", 1, DateTime.UtcNow);
        _reservations.Stored = reservation;

        var response = await UserClient().PostAsync($"{Reservations}/{reservation.Id}/cancel", null);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task UnknownProduct_Returns404_ProductNotFound()
    {
        _reservations.Outcome = StockReservationOutcome.ProductNotFound;

        var response = await UserClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 1 });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "PRODUCT_NOT_FOUND");
    }

    [Fact]
    public async Task UnknownReservation_Returns404_ReservationNotFound()
    {
        var response = await UserClient().PostAsync($"{Reservations}/{Guid.NewGuid()}/confirm", null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "RESERVATION_NOT_FOUND");
    }

    [Fact]
    public async Task UnknownRoute_Returns404_NotFound()
    {
        var response = await UserClient().GetAsync("/api/v1/does-not-exist");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task InsufficientStock_Returns409_InsufficientStock()
    {
        _reservations.Outcome = StockReservationOutcome.InsufficientStock;

        var response = await UserClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 5 });

        var body = await AssertProblemAsync(response, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.Equal("Insufficient inventory", body.GetProperty("title").GetString());
        Assert.Contains("5 unit(s)", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task InvalidReservationState_Returns409_InvalidReservationState()
    {
        var reservation = Reservation.Create(Guid.NewGuid(), _user.Id.ToString(), 1, DateTime.UtcNow);
        reservation.Cancel(DateTime.UtcNow);
        _reservations.Stored = reservation;

        var response = await UserClient().PostAsync($"{Reservations}/{reservation.Id}/confirm", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "INVALID_RESERVATION_STATE");
    }

    [Fact]
    public async Task ExpiredReservation_Returns409_ReservationExpired()
    {
        _reservations.Stored = Reservation.Create(Guid.NewGuid(), _user.Id.ToString(), 1, DateTime.UtcNow.AddMinutes(-10));

        var response = await UserClient().PostAsync($"{Reservations}/{_reservations.Stored.Id}/confirm", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "RESERVATION_EXPIRED");
    }

    [Fact]
    public async Task LostRace_Returns409_ConcurrentlyModified()
    {
        _reservations.Stored = Reservation.Create(Guid.NewGuid(), _user.Id.ToString(), 1, DateTime.UtcNow);
        _reservations.TransitionSucceeds = false;

        var response = await UserClient().PostAsync($"{Reservations}/{_reservations.Stored.Id}/cancel", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "RESERVATION_CONCURRENTLY_MODIFIED");
    }

    [Fact]
    public async Task TemporarilyUnavailable_Returns503_WithRetryAfter()
    {
        _reservations.Failure = new Inventory.Application.Common.Exceptions.TemporarilyUnavailableException(
            "The inventory is busy right now. Nothing was changed; please retry shortly.",
            new InvalidOperationException(LeakyMessage));

        var response = await UserClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 1 });

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.DoesNotContain("SELECT", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnexpectedException_Returns500_WithoutLeakingInternals()
    {
        _reservations.Failure = new InvalidOperationException(LeakyMessage);

        var response = await UserClient().PostAsJsonAsync(Reservations, new { productId = Guid.NewGuid(), quantity = 1 });

        var body = await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var raw = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "SELECT", "Password", "hunter2", "Server=", "InvalidOperationException", " at ", "Scripted" })
            Assert.DoesNotContain(secret, raw);
        Assert.Contains("correlation id", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CorrelationId_InBody_MatchesResponseHeader_WhenPresent()
    {
        var response = await UserClient().PostAsync($"{Reservations}/{Guid.NewGuid()}/confirm", null);

        var body = await AssertProblemAsync(response, HttpStatusCode.NotFound, "RESERVATION_NOT_FOUND");
        if (response.Headers.TryGetValues("X-Correlation-ID", out var header))
            Assert.Equal(header.Single(), body.GetProperty("correlationId").GetString());
    }

    /// <summary>A reservation repository whose behaviour each test scripts.</summary>
    private sealed class ScriptedReservationRepository : IReservationRepository
    {
        public StockReservationOutcome Outcome { get; set; } = StockReservationOutcome.Reserved;
        public Reservation? Stored { get; set; }
        public bool TransitionSucceeds { get; set; } = true;
        public Exception? Failure { get; set; }

        public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Outcome) : Task.FromException<StockReservationOutcome>(Failure);

        public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Id == id ? Stored : null);

        public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) => Task.FromResult(TransitionSucceeds);

        public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) => Task.FromResult(TransitionSucceeds);

        public Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reservation>>([]);

        public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}

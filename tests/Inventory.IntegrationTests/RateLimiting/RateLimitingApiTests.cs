using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests.RateLimiting;

/// <summary>
/// The built-in rate limiter, with tiny limits so they trip quickly. Each test gets its own host,
/// hence its own limiter state.
/// </summary>
public class RateLimitingApiTests : IClassFixture<StrictRateLimitApiFactory>
{
    private readonly StrictRateLimitApiFactory _baseFactory;

    public RateLimitingApiTests(StrictRateLimitApiFactory factory) => _baseFactory = factory;

    private WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings) =>
        _baseFactory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IUserRepository, InMemoryUserRepository>();
                services.AddSingleton<IProductRepository, InMemoryProductRepository>();
                services.AddSingleton<IReservationRepository, UnusedReservationRepository>();
            });
        });

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.RetryAfter is not null, "Expected a Retry-After header.");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("type").GetString()));
        Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, body.RootElement.GetProperty("instance").GetString());
        Assert.Equal("RATE_LIMITED", body.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("correlationId").GetString()));
    }

    private static StringContent InvalidReservation() =>
        new("""{ "productId": "3f2b8f8e-0000-4000-8000-000000000001", "quantity": 0 }""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Login_ExceedingLimit_Returns429()
    {
        var host = HostWith(("RateLimiting:Login:PermitLimit", "3"), ("RateLimiting:Login:Window", "01:00:00"));
        var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "u", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        await AssertRateLimitedAsync(await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "u", password = "wrong" }));
    }

    [Fact]
    public async Task Reservations_ExceedingBurst_Returns429_ForThatUserOnly()
    {
        var host = HostWith(
            ("RateLimiting:Reservations:TokenLimit", "2"),
            ("RateLimiting:Reservations:TokensPerPeriod", "1"),
            ("RateLimiting:Reservations:ReplenishmentPeriod", "01:00:00"));
        var alice = host.CreateClientFor(UserRole.User);
        var bob = host.CreateClientFor(UserRole.User);

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/v1/reservations", InvalidReservation())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/v1/reservations", InvalidReservation())).StatusCode);
        await AssertRateLimitedAsync(await alice.PostAsync("/api/v1/reservations", InvalidReservation()));

        // A different user has their own bucket.
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/v1/reservations", InvalidReservation())).StatusCode);
    }

    [Fact]
    public async Task GeneralLimit_ExceedingIt_Returns429()
    {
        var host = HostWith(("RateLimiting:General:PermitLimit", "3"), ("RateLimiting:General:Window", "01:00:00"));
        var client = host.CreateClientFor(UserRole.User);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{Guid.NewGuid()}")).StatusCode);

        await AssertRateLimitedAsync(await client.GetAsync($"/api/v1/products/{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Health_IsNeverRateLimited()
    {
        var host = HostWith(("RateLimiting:General:PermitLimit", "1"), ("RateLimiting:General:Window", "01:00:00"));
        var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task DefaultLimits_AllowNormalUsage()
    {
        // appsettings.json values, untouched.
        var client = HostWith().CreateClientFor(UserRole.User);

        for (var i = 0; i < 50; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/reservations", InvalidReservation())).StatusCode);
    }
}

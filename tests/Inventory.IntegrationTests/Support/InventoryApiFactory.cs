using System.Net.Http.Headers;
using System.Security.Cryptography;
using Inventory.Application.Abstractions.Authentication;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests.Support;

/// <summary>
/// The API under test: background expiry worker off (so it cannot change data underneath tests; expiry is
/// exercised directly in <c>ReservationExpiryMySqlTests</c>) and a random per-run JWT signing key.
/// </summary>
public class InventoryApiFactory : WebApplicationFactory<Program>
{
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>
    /// Functional tests fire many requests from one test identity, so limits are lifted by default.
    /// Rate-limit tests (and the HTTP concurrency test) set this to <c>false</c> to run with the real configuration.
    /// </summary>
    public bool RelaxRateLimits { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ReservationExpiry:Enabled", "false");
        builder.UseSetting("Jwt:SigningKey", SigningKey);

        if (RelaxRateLimits)
        {
            builder.UseSetting("RateLimiting:General:PermitLimit", "1000000");
            builder.UseSetting("RateLimiting:Login:PermitLimit", "1000000");
            builder.UseSetting("RateLimiting:Reservations:TokenLimit", "1000000");
            builder.UseSetting("RateLimiting:Reservations:TokensPerPeriod", "1000000");
        }
    }
}

/// <summary>The API with the production rate limits from appsettings.json.</summary>
public sealed class StrictRateLimitApiFactory : InventoryApiFactory
{
    public StrictRateLimitApiFactory() => RelaxRateLimits = false;
}

public static class TestUsers
{
    public static User Create(UserRole role, string? username = null) =>
        User.Create(username ?? $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..20], "not-a-real-hash", role);
}

public static class AuthenticatedClientExtensions
{
    /// <summary>Issues a real token through the app's own <see cref="IJwtTokenGenerator"/>.</summary>
    public static string CreateToken(this WebApplicationFactory<Program> factory, User user) =>
        factory.Services.GetRequiredService<IJwtTokenGenerator>().Generate(user).Token;

    public static HttpClient CreateClientFor(this WebApplicationFactory<Program> factory, User user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(user));
        return client;
    }

    public static HttpClient CreateClientFor(this WebApplicationFactory<Program> factory, UserRole role) =>
        factory.CreateClientFor(TestUsers.Create(role));
}

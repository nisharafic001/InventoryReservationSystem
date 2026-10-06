using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Auth;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Inventory.IntegrationTests.Observability;

/// <summary>Correlation ids and structured request logging, captured from the real Serilog pipeline.</summary>
public class LoggingApiTests : IClassFixture<InventoryApiFactory>
{
    private const string Password = "Sup3r-S3cret-Pa55!";
    private const string CorrelationHeader = "X-Correlation-ID";

    private readonly CapturingSink _sink = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryProductRepository _products = new();
    private readonly string _signingKey;

    public LoggingApiTests(InventoryApiFactory factory)
    {
        _signingKey = factory.SigningKey;
        var users = new InMemoryUserRepository();
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(_sink);
            services.AddSingleton<IUserRepository>(users);
            services.AddSingleton<IProductRepository>(_products);
        }));

        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        users.AddAsync(User.Create("auditor", hasher.Hash(Password), UserRole.Admin), default).Wait();
    }

    private LogEvent RequestCompletedEvent(string path) =>
        Assert.Single(_sink.Events, e =>
            e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod}", StringComparison.Ordinal)
            && Scalar(e, "RequestPath") == path);

    private static string? Scalar(LogEvent e, string property) =>
        e.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: var v } ? v?.ToString() : null;

    [Fact]
    public async Task Response_HasGeneratedCorrelationId()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        var id = Assert.Single(response.Headers.GetValues(CorrelationHeader));
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task WellFormedIncomingCorrelationId_IsEchoedAndUsedInProblemDetails()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, "client-req-42");

        var response = await client.GetAsync($"/api/v1/products/{Guid.NewGuid()}"); // 401: no token

        Assert.Equal("client-req-42", Assert.Single(response.Headers.GetValues(CorrelationHeader)));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("client-req-42", body.RootElement.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("bad id with spaces")]
    [InlineData("evil\"quote")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task UnsafeIncomingCorrelationId_IsReplaced(string unsafeId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeader, unsafeId);

        var response = await client.GetAsync("/health");

        var id = Assert.Single(response.Headers.GetValues(CorrelationHeader));
        Assert.NotEqual(unsafeId, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task RequestCompletionEvent_HasCorrelationMethodPathStatusAndElapsed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, "trace-me-1");

        await client.GetAsync("/health");

        var e = RequestCompletedEvent("/health");
        Assert.Equal("trace-me-1", Scalar(e, "CorrelationId"));
        Assert.Equal("GET", Scalar(e, "RequestMethod"));
        Assert.Equal("200", Scalar(e, "StatusCode"));
        Assert.True(double.TryParse(Scalar(e, "Elapsed"), out var elapsed) && elapsed >= 0);
    }

    [Fact]
    public async Task ProductRequests_LogProductIdAndUserId()
    {
        var admin = TestUsers.Create(UserRole.Admin);
        var client = _factory.CreateClientFor(admin);
        var product = Product.Create("LOG-SKU-1", "Logged", 3, DateTime.UtcNow);
        await _products.AddAsync(product, default);

        await client.GetAsync($"/api/v1/products/{product.Id}");

        var e = RequestCompletedEvent($"/api/v1/products/{product.Id}");
        Assert.Equal(product.Id.ToString(), Scalar(e, "ProductId"));
        Assert.Equal(admin.Id.ToString(), Scalar(e, "UserId"));
        Assert.Equal("200", Scalar(e, "StatusCode"));
    }

    [Fact]
    public async Task BusinessEvents_CarryCorrelationIdAndEntityIds()
    {
        var client = _factory.CreateClientFor(TestUsers.Create(UserRole.Admin));
        client.DefaultRequestHeaders.Add(CorrelationHeader, "create-prod-7");

        var response = await client.PostAsJsonAsync("/api/v1/products", new { sku = "LOG-SKU-2", name = "Logged", totalQuantity = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.Single(_sink.Events, e => e.MessageTemplate.Text.StartsWith("Product {ProductId} created", StringComparison.Ordinal));
        Assert.Equal("create-prod-7", Scalar(created, "CorrelationId"));
        Assert.Equal("LOG-SKU-2", Scalar(created, "Sku"));
    }

    [Fact]
    public async Task Logs_NeverContainPasswordsTokensOrAuthorizationHeaders()
    {
        var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { username = "auditor", password = Password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { username = "auditor", password = Password + "-wrong" });

        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await authed.GetAsync($"/api/v1/products/{Guid.NewGuid()}");
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token + "tampered");
        await authed.GetAsync($"/api/v1/products/{Guid.NewGuid()}");

        Assert.NotEmpty(_sink.Events);
        var everything = string.Join('\n', _sink.Events.Select(e =>
            e.RenderMessage() + " " + string.Join(' ', e.Properties.Select(p => $"{p.Key}={p.Value}")) + " " + e.Exception));

        Assert.DoesNotContain(Password, everything);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(token.Split('.')[2], everything); // not even the signature part
        Assert.DoesNotContain("Bearer ey", everything);
        Assert.DoesNotContain("Authorization", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_signingKey, everything);

        // ...while still recording that the login attempts happened.
        Assert.Contains(_sink.Events, e => e.MessageTemplate.Text.StartsWith("User {UserId} ({Username}) logged in", StringComparison.Ordinal));
        Assert.Contains(_sink.Events, e => e.MessageTemplate.Text.StartsWith("Login failed for username", StringComparison.Ordinal));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}

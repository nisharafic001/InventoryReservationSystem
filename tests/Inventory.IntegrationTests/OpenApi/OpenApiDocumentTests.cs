using System.Net;
using System.Text.Json;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace Inventory.IntegrationTests.OpenApi;

/// <summary>The generated OpenAPI document describes endpoints, schemas, responses and JWT security accurately.</summary>
public class OpenApiDocumentTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private const string SwaggerJson = "/swagger/v1/swagger.json";

    private async Task<JsonElement> GetDocumentAsync()
    {
        var response = await factory.CreateClient().GetAsync(SwaggerJson);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static JsonElement Operation(JsonElement doc, string path, string method) =>
        doc.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static string[] ResponseCodes(JsonElement operation) =>
        operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).Order().ToArray();

    private static bool RequiresBearer(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
        && security.EnumerateArray().Any(requirement => requirement.TryGetProperty("Bearer", out _));

    [Fact]
    public async Task Document_DefinesJwtBearerSecurityScheme()
    {
        var doc = await GetDocumentAsync();

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        Assert.Equal("Inventory Reservation API", doc.GetProperty("info").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/api/v1/auth/login", "post")]
    [InlineData("/api/v1/products", "post")]
    [InlineData("/api/v1/products/{id}", "get")]
    [InlineData("/api/v1/reservations", "post")]
    [InlineData("/api/v1/reservations/{id}/confirm", "post")]
    [InlineData("/api/v1/reservations/{id}/cancel", "post")]
    public async Task EveryEndpoint_HasSummary(string path, string method)
    {
        var operation = Operation(await GetDocumentAsync(), path, method);

        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
    }

    [Fact]
    public async Task Health_IsDocumented_AnonymousAndUnthrottled()
    {
        var health = Operation(await GetDocumentAsync(), "/health", "get");

        Assert.Equal("Health", health.GetProperty("tags")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("summary").GetString()));
        Assert.False(RequiresBearer(health));
        Assert.Equal(["200"], ResponseCodes(health));
    }

    [Fact]
    public async Task Login_IsAnonymous_WithDocumentedResponses()
    {
        var login = Operation(await GetDocumentAsync(), "/api/v1/auth/login", "post");

        Assert.False(RequiresBearer(login));
        Assert.Equal(["200", "400", "401", "429"], ResponseCodes(login));
        Assert.Contains("Authorize", login.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("/api/v1/products", "post", new[] { "201", "400", "401", "403", "409", "429" })]
    [InlineData("/api/v1/products/{id}", "get", new[] { "200", "401", "404", "429" })]
    [InlineData("/api/v1/reservations", "post", new[] { "201", "400", "401", "404", "409", "429" })]
    [InlineData("/api/v1/reservations/{id}/confirm", "post", new[] { "200", "401", "403", "404", "409", "429" })]
    [InlineData("/api/v1/reservations/{id}/cancel", "post", new[] { "200", "401", "403", "404", "409", "429" })]
    public async Task ProtectedEndpoints_RequireBearer_AndDocumentResponseCodes(string path, string method, string[] codes)
    {
        var operation = Operation(await GetDocumentAsync(), path, method);

        Assert.True(RequiresBearer(operation), $"{method} {path} should show the padlock.");
        Assert.Equal(codes, ResponseCodes(operation));
    }

    [Fact]
    public async Task AdminEndpoint_IsMarkedAdminOnly()
    {
        var create = Operation(await GetDocumentAsync(), "/api/v1/products", "post");

        Assert.Contains("Admin", create.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ErrorResponses_UseProblemJson()
    {
        var reserve = Operation(await GetDocumentAsync(), "/api/v1/reservations", "post");

        foreach (var code in new[] { "400", "401", "404", "409", "429" })
            Assert.True(
                reserve.GetProperty("responses").GetProperty(code).GetProperty("content").TryGetProperty("application/problem+json", out _),
                $"{code} should be application/problem+json");
    }

    [Theory]
    [InlineData("CreateProductRequest", new[] { "name", "sku", "totalQuantity" })]
    [InlineData("CreateReservationRequest", new[] { "productId", "quantity" })]
    [InlineData("LoginRequest", new[] { "password", "username" })]
    [InlineData("LoginResponse", new[] { "accessToken", "expiresAtUtc", "tokenType" })]
    [InlineData("ProductResponse", new[] { "availableQuantity", "id", "name", "reservedQuantity", "sku", "soldQuantity", "totalQuantity" })]
    [InlineData("ReservationResponse", new[] { "cancelledAtUtc", "confirmedAtUtc", "createdAtUtc", "expiresAtUtc", "productId", "quantity", "reservationId", "status" })]
    public async Task Schemas_MatchTheJsonContract_AndAreDescribed(string schemaName, string[] properties)
    {
        var schema = (await GetDocumentAsync()).GetProperty("components").GetProperty("schemas").GetProperty(schemaName);

        var props = schema.GetProperty("properties");
        Assert.Equal(properties, props.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.All(props.EnumerateObject(), p =>
            Assert.False(string.IsNullOrWhiteSpace(p.Value.TryGetProperty("description", out var d) ? d.GetString() : null),
                $"{schemaName}.{p.Name} needs a description"));
    }

    [Fact]
    public async Task ReservationStatus_IsDocumentedAsStrings()
    {
        var schemas = (await GetDocumentAsync()).GetProperty("components").GetProperty("schemas");

        var values = schemas.GetProperty("ReservationStatus").GetProperty("enum").EnumerateArray().Select(v => v.GetString());
        Assert.Equal(["Active", "Confirmed", "Cancelled", "Expired"], values);
    }

    [Fact]
    public async Task SwaggerUi_IsServed_WithoutAuthentication()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("swagger-ui", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_IsOff_InProduction_UnlessEnabled()
    {
        var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var optedIn = factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").UseSetting("Swagger:Enabled", "true"));

        Assert.NotEqual(HttpStatusCode.OK, (await production.CreateClient().GetAsync(SwaggerJson)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await optedIn.CreateClient().GetAsync(SwaggerJson)).StatusCode);
    }
}

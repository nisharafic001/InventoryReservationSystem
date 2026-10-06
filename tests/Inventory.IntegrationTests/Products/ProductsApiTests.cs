using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Products;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.IntegrationTests.Products;

/// <summary>
/// HTTP-level tests of the products API (routing, status codes, contract, error mapping)
/// with persistence replaced by an in-memory repository.
/// </summary>
public class ProductsApiTests : IClassFixture<InventoryApiFactory>
{
    private const string BaseUrl = "/api/v1/products";
    private readonly HttpClient _client;

    public ProductsApiTests(InventoryApiFactory factory)
    {
        _client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IProductRepository, InMemoryProductRepository>()))
            .CreateClientFor(UserRole.Admin);
    }

    private static string UniqueSku() => $"SKU-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Post_ValidProduct_Returns201WithLocationAndBody()
    {
        var sku = UniqueSku();

        var response = await _client.PostAsJsonAsync(BaseUrl, new { sku, name = "iPhone", totalQuantity = 10 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(product);
        Assert.Equal(sku, product.Sku);
        Assert.Equal("iPhone", product.Name);
        Assert.Equal(10, product.TotalQuantity);
        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(0, product.SoldQuantity);
        Assert.Equal(10, product.AvailableQuantity);
        Assert.Equal($"{BaseUrl}/{product.Id}", response.Headers.Location?.AbsolutePath, ignoreCase: true);
    }

    [Fact]
    public async Task Post_ResponseUsesCamelCaseContract()
    {
        var response = await _client.PostAsJsonAsync(BaseUrl, new { sku = UniqueSku(), name = "iPhone", totalQuantity = 1 });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).Order();

        Assert.Equal(
            ["availableQuantity", "id", "name", "reservedQuantity", "sku", "soldQuantity", "totalQuantity"],
            properties);
    }

    [Fact]
    public async Task Get_ExistingProduct_Returns200()
    {
        var created = await (await _client.PostAsJsonAsync(BaseUrl, new { sku = UniqueSku(), name = "iPhone", totalQuantity = 3 }))
            .Content.ReadFromJsonAsync<ProductResponse>();

        var response = await _client.GetAsync($"{BaseUrl}/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created, await response.Content.ReadFromJsonAsync<ProductResponse>());
    }

    [Fact]
    public async Task Get_UnknownProduct_Returns404ProblemDetails()
    {
        var response = await _client.GetAsync($"{BaseUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_NonGuidId_Returns404()
    {
        var response = await _client.GetAsync($"{BaseUrl}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("""{ "name": "iPhone", "totalQuantity": 1 }""", "sku")]
    [InlineData("""{ "sku": "", "name": "iPhone", "totalQuantity": 1 }""", "sku")]
    [InlineData("""{ "sku": "SKU-X", "totalQuantity": 1 }""", "name")]
    [InlineData("""{ "sku": "SKU-X", "name": "iPhone" }""", "totalQuantity")]
    [InlineData("""{ "sku": "SKU-X", "name": "iPhone", "totalQuantity": -1 }""", "totalQuantity")]
    public async Task Post_InvalidField_Returns400WithFieldError(string body, string field)
    {
        var response = await _client.PostAsync(BaseUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"Expected an error for {field}.");
    }

    [Fact]
    public async Task Post_MalformedJson_Returns400()
    {
        var response = await _client.PostAsync(BaseUrl, new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_DuplicateSku_Returns409()
    {
        var sku = UniqueSku();
        await _client.PostAsJsonAsync(BaseUrl, new { sku, name = "First", totalQuantity = 1 });

        var response = await _client.PostAsJsonAsync(BaseUrl, new { sku = sku.ToLowerInvariant(), name = "Second", totalQuantity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

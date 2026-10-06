using Microsoft.Extensions.Logging.Abstractions;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common.Exceptions;
using Inventory.Application.Products;
using Inventory.Domain.Entities;

namespace Inventory.UnitTests.Application;

public class ProductServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryProductRepository _repository = new();
    private readonly ProductService _service;

    public ProductServiceTests()
    {
        _service = new ProductService(_repository, new FixedTimeProvider(Now), NullLogger<ProductService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsAndReturnsProduct()
    {
        var response = await _service.CreateAsync(new CreateProductRequest("IPHONE-001", "iPhone", 10), default);

        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal("IPHONE-001", response.Sku);
        Assert.Equal("iPhone", response.Name);
        Assert.Equal(10, response.TotalQuantity);
        Assert.Equal(0, response.ReservedQuantity);
        Assert.Equal(0, response.SoldQuantity);
        Assert.Equal(10, response.AvailableQuantity);

        var stored = Assert.Single(_repository.Products);
        Assert.Equal(response.Id, stored.Id);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateAsync_TrimsSkuAndName()
    {
        var response = await _service.CreateAsync(new CreateProductRequest("  SKU-1 ", " Widget  ", 0), default);

        Assert.Equal("SKU-1", response.Sku);
        Assert.Equal("Widget", response.Name);
    }

    [Theory]
    [InlineData(null, "iPhone", 1, "Sku")]
    [InlineData("  ", "iPhone", 1, "Sku")]
    [InlineData("SKU-1", null, 1, "Name")]
    [InlineData("SKU-1", "", 1, "Name")]
    [InlineData("SKU-1", "iPhone", null, "TotalQuantity")]
    [InlineData("SKU-1", "iPhone", -1, "TotalQuantity")]
    public async Task CreateAsync_InvalidField_ThrowsValidationException(
        string? sku, string? name, int? totalQuantity, string invalidField)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateAsync(new CreateProductRequest(sku, name, totalQuantity), default));

        Assert.Equal([invalidField], ex.Errors.Keys);
        Assert.Empty(_repository.Products);
    }

    [Fact]
    public async Task CreateAsync_TooLongFields_ThrowsValidationException()
    {
        var request = new CreateProductRequest(
            new string('S', Product.SkuMaxLength + 1),
            new string('N', Product.NameMaxLength + 1),
            1);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(request, default));

        Assert.Contains("Sku", ex.Errors.Keys);
        Assert.Contains("Name", ex.Errors.Keys);
    }

    [Fact]
    public async Task CreateAsync_ReportsAllInvalidFieldsAtOnce()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateAsync(new CreateProductRequest(null, null, -5), default));

        Assert.Equal(3, ex.Errors.Count);
    }

    [Fact]
    public async Task CreateAsync_DuplicateSku_ThrowsDuplicateSkuException()
    {
        await _service.CreateAsync(new CreateProductRequest("SKU-1", "First", 1), default);

        var ex = await Assert.ThrowsAsync<DuplicateSkuException>(
            () => _service.CreateAsync(new CreateProductRequest("SKU-1", "Second", 1), default));

        Assert.Equal("SKU-1", ex.Sku);
        Assert.Single(_repository.Products);
    }

    [Fact]
    public async Task GetByIdAsync_Existing_ReturnsProduct()
    {
        var created = await _service.CreateAsync(new CreateProductRequest("SKU-1", "Widget", 4), default);

        var found = await _service.GetByIdAsync(created.Id, default);

        Assert.Equal(created, found);
    }

    [Fact]
    public async Task GetByIdAsync_Missing_ReturnsNull()
    {
        Assert.Null(await _service.GetByIdAsync(Guid.NewGuid(), default));
    }

    private sealed class InMemoryProductRepository : IProductRepository
    {
        public List<Product> Products { get; } = [];

        public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Products.SingleOrDefault(p => p.Id == id));

        public Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken) =>
            Task.FromResult(Products.Any(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase)));

        public Task AddAsync(Product product, CancellationToken cancellationToken)
        {
            Products.Add(product);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

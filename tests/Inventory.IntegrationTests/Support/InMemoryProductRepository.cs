using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Products;
using Inventory.Domain.Entities;

namespace Inventory.IntegrationTests.Support;

/// <summary>
/// Thread-safe stand-in for the MySQL repository, mirroring its case-insensitive unique SKU.
/// </summary>
public sealed class InMemoryProductRepository : IProductRepository
{
    private readonly Lock _gate = new();
    private readonly List<Product> _products = [];

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_products.SingleOrDefault(p => p.Id == id));
    }

    public Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_products.Any(p => SameSku(p.Sku, sku)));
    }

    public Task AddAsync(Product product, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_products.Any(p => SameSku(p.Sku, product.Sku)))
                throw new DuplicateSkuException(product.Sku);

            _products.Add(product);
        }

        return Task.CompletedTask;
    }

    private static bool SameSku(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

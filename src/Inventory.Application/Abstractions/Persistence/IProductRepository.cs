using Inventory.Domain.Entities;

namespace Inventory.Application.Abstractions.Persistence;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new product.
    /// </summary>
    /// <exception cref="Products.DuplicateSkuException">The SKU is already taken.</exception>
    Task AddAsync(Product product, CancellationToken cancellationToken);
}

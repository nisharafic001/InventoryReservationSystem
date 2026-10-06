namespace Inventory.Application.Products;

public interface IProductService
{
    /// <exception cref="Common.Exceptions.ValidationException">The request is invalid.</exception>
    /// <exception cref="DuplicateSkuException">The SKU is already taken.</exception>
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);

    /// <returns>The product, or <c>null</c> if it does not exist.</returns>
    Task<ProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}

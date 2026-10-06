using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common;
using Inventory.Application.Common.Exceptions;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Products;

public sealed class ProductService(
    IProductRepository products,
    TimeProvider timeProvider,
    ILogger<ProductService> logger) : IProductService
{
    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var sku = request.Sku!.Trim();
        if (await products.SkuExistsAsync(sku, cancellationToken))
            throw new DuplicateSkuException(sku);

        var product = Product.Create(sku, request.Name!, request.TotalQuantity!.Value, timeProvider.GetUtcNowDateTime());

        // The unique index still guards against a concurrent insert of the same SKU.
        await products.AddAsync(product, cancellationToken);
        logger.LogInformation(
            "Product {ProductId} created with SKU {Sku}, total quantity {TotalQuantity}.", product.Id, product.Sku, product.TotalQuantity);

        return ProductResponse.From(product);
    }

    public async Task<ProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(id, cancellationToken);
        return product is null ? null : ProductResponse.From(product);
    }

    private static void Validate(CreateProductRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Sku))
            errors[nameof(request.Sku)] = ["SKU is required."];
        else if (request.Sku.Trim().Length > Product.SkuMaxLength)
            errors[nameof(request.Sku)] = [$"SKU cannot exceed {Product.SkuMaxLength} characters."];

        if (string.IsNullOrWhiteSpace(request.Name))
            errors[nameof(request.Name)] = ["Name is required."];
        else if (request.Name.Trim().Length > Product.NameMaxLength)
            errors[nameof(request.Name)] = [$"Name cannot exceed {Product.NameMaxLength} characters."];

        if (request.TotalQuantity is null)
            errors[nameof(request.TotalQuantity)] = ["Total quantity is required."];
        else if (request.TotalQuantity < 0)
            errors[nameof(request.TotalQuantity)] = ["Total quantity cannot be negative."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }
}

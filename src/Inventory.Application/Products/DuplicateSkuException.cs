using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Products;

public sealed class DuplicateSkuException(string sku)
    : ConflictException($"A product with SKU '{sku}' already exists.")
{
    public string Sku { get; } = sku;
}

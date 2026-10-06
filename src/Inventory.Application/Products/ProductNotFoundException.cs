using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Products;

public sealed class ProductNotFoundException(Guid productId)
    : NotFoundException($"Product '{productId}' was not found.")
{
    public Guid ProductId { get; } = productId;
}

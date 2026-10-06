using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Reservations;

public sealed class InsufficientStockException(Guid productId, int requestedQuantity)
    : ConflictException($"Product '{productId}' does not have {requestedQuantity} unit(s) available.")
{
    public Guid ProductId { get; } = productId;
    public int RequestedQuantity { get; } = requestedQuantity;
}

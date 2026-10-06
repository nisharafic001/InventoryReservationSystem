namespace Inventory.Domain.Exceptions;

public sealed class InsufficientStockException(Guid productId, int requested, int available)
    : DomainException($"Product '{productId}' has {available} available, but {requested} was requested.")
{
    public Guid ProductId { get; } = productId;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}

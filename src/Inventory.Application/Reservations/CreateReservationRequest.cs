namespace Inventory.Application.Reservations;

/// <summary>Stock to reserve.</summary>
/// <param name="ProductId">The product to reserve (a GUID from <c>POST /api/v1/products</c>).</param>
/// <param name="Quantity">Units to reserve; greater than zero.</param>
// Members are nullable so missing fields reach validation instead of defaulting silently.
public sealed record CreateReservationRequest(Guid? ProductId, int? Quantity);

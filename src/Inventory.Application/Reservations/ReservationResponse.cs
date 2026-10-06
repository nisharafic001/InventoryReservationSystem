using Inventory.Domain.Entities;
using Inventory.Domain.Enums;

namespace Inventory.Application.Reservations;

/// <summary>A reservation and its lifecycle state.</summary>
/// <param name="ReservationId">Reservation id; use it to confirm or cancel.</param>
/// <param name="ProductId">The reserved product.</param>
/// <param name="Quantity">Units held.</param>
/// <param name="Status"><c>Active</c>, <c>Confirmed</c>, <c>Cancelled</c> or <c>Expired</c>.</param>
/// <param name="CreatedAtUtc">When the stock was reserved (UTC).</param>
/// <param name="ExpiresAtUtc">Two minutes after creation; an unconfirmed reservation is released then (UTC).</param>
/// <param name="ConfirmedAtUtc">When it was confirmed, if it was (UTC).</param>
/// <param name="CancelledAtUtc">When it was cancelled, if it was (UTC).</param>
public sealed record ReservationResponse(
    Guid ReservationId,
    Guid ProductId,
    int Quantity,
    ReservationStatus Status,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? ConfirmedAtUtc,
    DateTime? CancelledAtUtc)
{
    public static ReservationResponse From(Reservation reservation) => new(
        reservation.Id,
        reservation.ProductId,
        reservation.Quantity,
        reservation.Status,
        reservation.CreatedAtUtc,
        reservation.ExpiresAtUtc,
        reservation.ConfirmedAtUtc,
        reservation.CancelledAtUtc);
}

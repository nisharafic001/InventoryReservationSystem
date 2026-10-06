using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Reservations;

/// <summary>
/// The reservation left the Active state (confirmed, cancelled or expired by another request)
/// between being read and being written.
/// </summary>
public sealed class ReservationConcurrencyException(Guid reservationId)
    : ConflictException($"Reservation '{reservationId}' is no longer active; it was changed by another request.")
{
    public Guid ReservationId { get; } = reservationId;
}

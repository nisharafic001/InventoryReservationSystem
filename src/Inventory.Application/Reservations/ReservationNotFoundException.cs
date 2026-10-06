using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Reservations;

public sealed class ReservationNotFoundException(Guid reservationId)
    : NotFoundException($"Reservation '{reservationId}' was not found.")
{
    public Guid ReservationId { get; } = reservationId;
}

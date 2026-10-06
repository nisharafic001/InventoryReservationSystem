using Inventory.Domain.Enums;

namespace Inventory.Domain.Exceptions;

public sealed class InvalidReservationTransitionException(Guid reservationId, ReservationStatus from, ReservationStatus to)
    : DomainException(from == to
        ? $"Reservation '{reservationId}' is already {from}."
        : $"Reservation '{reservationId}' is {from} and cannot be {Verb(to)}.")
{
    public Guid ReservationId { get; } = reservationId;
    public ReservationStatus From { get; } = from;
    public ReservationStatus To { get; } = to;

    private static string Verb(ReservationStatus to) => to switch
    {
        ReservationStatus.Confirmed => "confirmed",
        ReservationStatus.Cancelled => "cancelled",
        ReservationStatus.Expired => "expired",
        _ => to.ToString().ToLowerInvariant()
    };
}

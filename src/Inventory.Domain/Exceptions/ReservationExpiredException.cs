namespace Inventory.Domain.Exceptions;

public sealed class ReservationExpiredException(Guid reservationId, DateTime expiresAtUtc)
    : DomainException($"Reservation '{reservationId}' expired at {expiresAtUtc:O} and cannot be confirmed.")
{
    public Guid ReservationId { get; } = reservationId;
    public DateTime ExpiresAtUtc { get; } = expiresAtUtc;
}

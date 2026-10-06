using Inventory.Domain.Enums;
using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Entities;

public sealed class Reservation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    // Parameter names match property names so EF Core can bind this constructor when materializing.
    private Reservation(Guid id, Guid productId, string userId, int quantity, DateTime createdAtUtc)
    {
        Id = id;
        ProductId = productId;
        UserId = userId;
        Quantity = quantity;
        Status = ReservationStatus.Active;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = createdAtUtc.Add(Lifetime);
    }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string UserId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }

    public static Reservation Create(Guid productId, string userId, int quantity, DateTime nowUtc)
    {
        if (productId == Guid.Empty)
            throw new DomainException("Product id is required.");
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("User id is required.");
        if (quantity <= 0)
            throw new DomainException("Reservation quantity must be greater than zero.");

        return new Reservation(Guid.NewGuid(), productId, userId, quantity, nowUtc);
    }

    /// <summary>True when the reservation is still Active but its lifetime has elapsed.</summary>
    public bool IsExpiredAt(DateTime nowUtc) =>
        Status == ReservationStatus.Active && nowUtc >= ExpiresAtUtc;

    public void Confirm(DateTime nowUtc)
    {
        // Same answer whether or not the expiry worker has already run: a late confirm is "expired".
        if (Status == ReservationStatus.Expired || IsExpiredAt(nowUtc))
            throw new ReservationExpiredException(Id, ExpiresAtUtc);
        EnsureActive(ReservationStatus.Confirmed);

        Status = ReservationStatus.Confirmed;
        ConfirmedAtUtc = nowUtc;
    }

    public void Cancel(DateTime nowUtc)
    {
        EnsureActive(ReservationStatus.Cancelled);

        Status = ReservationStatus.Cancelled;
        CancelledAtUtc = nowUtc;
    }

    public void Expire(DateTime nowUtc)
    {
        EnsureActive(ReservationStatus.Expired);
        if (!IsExpiredAt(nowUtc))
            throw new DomainException($"Reservation '{Id}' does not expire until {ExpiresAtUtc:O}.");

        Status = ReservationStatus.Expired;
    }

    // Active is the only non-terminal state, so every transition must start from it.
    private void EnsureActive(ReservationStatus target)
    {
        if (Status != ReservationStatus.Active)
            throw new InvalidReservationTransitionException(Id, Status, target);
    }
}

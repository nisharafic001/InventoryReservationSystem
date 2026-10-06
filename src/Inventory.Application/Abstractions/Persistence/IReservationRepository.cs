using Inventory.Domain.Entities;

namespace Inventory.Application.Abstractions.Persistence;

public enum StockReservationOutcome
{
    Reserved,
    ProductNotFound,
    InsufficientStock
}

public interface IReservationRepository
{
    /// <summary>
    /// Atomically holds <see cref="Reservation.Quantity"/> units of the product's stock and persists the reservation,
    /// in a single transaction. Nothing is persisted unless the outcome is <see cref="StockReservationOutcome.Reserved"/>.
    /// </summary>
    Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken);

    /// <summary>Loads a reservation without tracking it.</summary>
    Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a reservation already transitioned to Confirmed and moves its quantity from reserved to sold,
    /// in one transaction. The write only applies if the stored reservation is still Active and unexpired.
    /// </summary>
    /// <returns><c>false</c> if another request changed the reservation first; nothing is persisted.</returns>
    Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a reservation already transitioned to Cancelled and releases its reserved quantity,
    /// in one transaction. The write only applies if the stored reservation is still Active.
    /// </summary>
    /// <returns><c>false</c> if another request changed the reservation first; nothing is persisted.</returns>
    Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken);

    /// <summary>
    /// Active reservations whose expiry is at or before <paramref name="nowUtc"/>, oldest first, without tracking.
    /// </summary>
    Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a reservation already transitioned to Expired and releases its reserved quantity, in one
    /// transaction. The write only applies if the stored reservation is still Active and due at <paramref name="nowUtc"/>.
    /// </summary>
    /// <returns><c>false</c> if another request or instance changed the reservation first; nothing is persisted.</returns>
    Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken);
}

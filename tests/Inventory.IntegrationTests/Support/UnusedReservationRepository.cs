using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;

namespace Inventory.IntegrationTests.Support;

/// <summary>For tests whose requests must never reach reservation persistence (e.g. validation or throttling).</summary>
public sealed class UnusedReservationRepository : IReservationRepository
{
    private static InvalidOperationException Unexpected() => new("This test must not reach the reservation repository.");

    public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken) => throw Unexpected();

    public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw Unexpected();

    public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) => throw Unexpected();

    public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) => throw Unexpected();

    public Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken) =>
        throw Unexpected();

    public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken) => throw Unexpected();
}

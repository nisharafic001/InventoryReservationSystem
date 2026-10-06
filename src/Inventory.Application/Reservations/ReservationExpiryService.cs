using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Reservations;

public sealed class ReservationExpiryService(
    IReservationRepository reservations,
    TimeProvider timeProvider,
    ILogger<ReservationExpiryService> logger)
    : IReservationExpiryService
{
    public async Task<ExpiryBatchResult> ExpireDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var nowUtc = timeProvider.GetUtcNowDateTime();
        var due = await reservations.GetDueForExpiryAsync(nowUtc, batchSize, cancellationToken);

        int expired = 0, failed = 0;
        foreach (var reservation in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                reservation.Expire(nowUtc);

                // Another instance, or a confirm/cancel, may have changed it since it was read; that is not an error.
                if (await reservations.ExpireAsync(reservation, nowUtc, cancellationToken))
                {
                    expired++;
                    logger.LogInformation(
                        "Reservation {ReservationId} expired; released {Quantity} unit(s) of product {ProductId}.",
                        reservation.Id, reservation.Quantity, reservation.ProductId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolate the failure: one bad row must not stop every later reservation from expiring.
                failed++;
                logger.LogError(ex,
                    "Could not expire reservation {ReservationId} of product {ProductId}; it will be retried on the next sweep.",
                    reservation.Id, reservation.ProductId);
            }
        }

        return new ExpiryBatchResult(due.Count, expired, failed);
    }
}

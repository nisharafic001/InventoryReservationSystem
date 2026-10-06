namespace Inventory.Application.Reservations;

/// <param name="Found">Due reservations read in this batch.</param>
/// <param name="Expired">How many of them this call expired (others were changed concurrently or failed).</param>
/// <param name="Failed">How many could not be expired because of an error; they are retried on the next sweep.</param>
public sealed record ExpiryBatchResult(int Found, int Expired, int Failed = 0);

public interface IReservationExpiryService
{
    /// <summary>
    /// Expires up to <paramref name="batchSize"/> Active reservations whose lifetime has elapsed and releases their stock.
    /// Safe to run concurrently from any number of instances. A failure on one reservation is logged and does not
    /// prevent the others from being expired.
    /// </summary>
    Task<ExpiryBatchResult> ExpireDueAsync(int batchSize, CancellationToken cancellationToken);
}

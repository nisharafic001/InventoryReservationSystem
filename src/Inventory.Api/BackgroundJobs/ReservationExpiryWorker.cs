using Inventory.Application.Reservations;
using Microsoft.Extensions.Options;

namespace Inventory.Api.BackgroundJobs;

/// <summary>
/// Periodically expires Active reservations past their lifetime and releases their stock.
/// Every instance may run this: correctness comes from conditional database transitions, not from coordination.
/// </summary>
public sealed class ReservationExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReservationExpiryOptions> options,
    TimeProvider timeProvider,
    ILogger<ReservationExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Reservation expiry worker is disabled.");
            return;
        }

        logger.LogInformation(
            "Reservation expiry worker started (interval {Interval}, batch size {BatchSize}).",
            settings.PollingInterval, settings.BatchSize);

        using var timer = new PeriodicTimer(settings.PollingInterval, timeProvider);
        try
        {
            do
            {
                await SweepAsync(settings.BatchSize, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: the in-flight transaction (if any) was rolled back by cancellation.
        }

        logger.LogInformation("Reservation expiry worker stopped.");
    }

    /// <summary>Runs one sweep, draining full batches. Failures are logged so the next tick retries.</summary>
    internal async Task SweepAsync(int batchSize, CancellationToken stoppingToken)
    {
        try
        {
            ExpiryBatchResult batch;
            do
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var expiryService = scope.ServiceProvider.GetRequiredService<IReservationExpiryService>();

                batch = await expiryService.ExpireDueAsync(batchSize, stoppingToken);

                if (batch.Expired > 0)
                    logger.LogInformation("Expired {Count} reservation(s).", batch.Expired);
                if (batch.Failed > 0)
                    logger.LogWarning("{Count} reservation(s) could not be expired and will be retried.", batch.Failed);
            }
            // Keep draining full batches only while making progress; rows that keep failing wait for the next tick.
            while (batch.Found == batchSize && batch.Expired > 0 && !stoppingToken.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Reservation expiry sweep failed; retrying on the next tick.");
        }
    }
}

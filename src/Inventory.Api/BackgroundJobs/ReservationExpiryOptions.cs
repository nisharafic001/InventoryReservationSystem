namespace Inventory.Api.BackgroundJobs;

public sealed class ReservationExpiryOptions
{
    public const string SectionName = "ReservationExpiry";

    public bool Enabled { get; set; } = true;

    /// <summary>Time between sweeps. Expired stock is released at most this long after expiry.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Reservations processed per database round; a sweep keeps going while batches come back full.</summary>
    public int BatchSize { get; set; } = 100;
}

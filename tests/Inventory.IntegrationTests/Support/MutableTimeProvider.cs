namespace Inventory.IntegrationTests.Support;

/// <summary>
/// A clock tests can move. Only "now" is faked; timers (e.g. PeriodicTimer) still run on real time.
/// </summary>
public sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private long _utcTicks = utcNow.UtcTicks;

    public DateTimeOffset UtcNow
    {
        get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

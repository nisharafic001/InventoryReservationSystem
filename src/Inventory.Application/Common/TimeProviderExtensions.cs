namespace Inventory.Application.Common;

public static class TimeProviderExtensions
{
    /// <summary>
    /// Current UTC time truncated to whole microseconds — the precision persisted by the database —
    /// so values returned to clients match what is stored.
    /// </summary>
    public static DateTime GetUtcNowDateTime(this TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}

using Inventory.Application.Common;

namespace Inventory.UnitTests.Application;

public class TimeProviderExtensionsTests
{
    [Fact]
    public void GetUtcNowDateTime_TruncatesToMicrosecondsAndIsUtc()
    {
        // 7 sub-second digits; MySQL datetime(6) keeps only 6.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var value = new FixedTimeProvider(now).GetUtcNowDateTime();

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(now.UtcDateTime.AddTicks(-7), value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

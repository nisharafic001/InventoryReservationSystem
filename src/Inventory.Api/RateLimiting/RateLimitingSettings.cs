namespace Inventory.Api.RateLimiting;

/// <summary>
/// Bound from the <c>RateLimiting</c> section. Limits are per client (JWT subject, or remote IP when anonymous),
/// so they throttle abusive callers without capping how many different users can reserve at the same moment.
/// Rate limiting protects capacity only — inventory correctness is enforced by the database.
/// </summary>
public sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Every endpoint except health checks.</summary>
    public FixedWindowSettings General { get; set; } = new() { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Login attempts per client IP — slows down password guessing.</summary>
    public FixedWindowSettings Login { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Reservation operations per user: bursts up to <c>TokenLimit</c>, sustained <c>TokensPerPeriod</c>.</summary>
    public TokenBucketSettings Reservations { get; set; } = new()
    {
        TokenLimit = 60,
        TokensPerPeriod = 30,
        ReplenishmentPeriod = TimeSpan.FromSeconds(10)
    };

    public sealed class FixedWindowSettings
    {
        public int PermitLimit { get; set; }
        public TimeSpan Window { get; set; }
    }

    public sealed class TokenBucketSettings
    {
        public int TokenLimit { get; set; }
        public int TokensPerPeriod { get; set; }
        public TimeSpan ReplenishmentPeriod { get; set; }
    }

    internal bool IsValid() =>
        General is { PermitLimit: > 0 } && General.Window > TimeSpan.Zero
        && Login is { PermitLimit: > 0 } && Login.Window > TimeSpan.Zero
        && Reservations is { TokenLimit: > 0, TokensPerPeriod: > 0 } && Reservations.ReplenishmentPeriod > TimeSpan.Zero;
}

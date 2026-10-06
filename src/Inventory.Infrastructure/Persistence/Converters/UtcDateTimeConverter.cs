using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Inventory.Infrastructure.Persistence.Converters;

/// <summary>
/// Normalizes values to UTC on write and marks values read from the database as <see cref="DateTimeKind.Utc"/>.
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => ToUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    // Unspecified values are assumed to already be UTC.
    internal static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? UtcDateTimeConverter.ToUtc(v.Value) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

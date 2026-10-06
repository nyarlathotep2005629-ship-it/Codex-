using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => ToTicks(value), value => new DateTimeOffset(value, TimeSpan.Zero))
{
    private static long ToTicks(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new DomainValidationException("Persisted timestamp must be UTC.");
        return value.UtcTicks;
    }
}

internal sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
    value => value.HasValue ? ToTicks(value.Value) : null,
    value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
{
    private static long ToTicks(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new DomainValidationException("Persisted timestamp must be UTC.");
        return value.UtcTicks;
    }
}

internal sealed class DayNumberConverter() : ValueConverter<DateOnly, int>(
    value => value.DayNumber, value => DateOnly.FromDayNumber(value));

internal sealed class WeightHundredthsKgConverter() : ValueConverter<decimal?, long?>(
    value => value.HasValue ? WeightValue.ToHundredthsKg(value.Value) : null,
    value => value.HasValue ? WeightValue.FromHundredthsKg(value.Value) : null);


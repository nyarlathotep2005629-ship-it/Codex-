namespace WorkoutTracker.Domain;

public abstract class EntityBase
{
    protected EntityBase() { }

    protected EntityBase(Guid id, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Id must not be empty.");
        Guard.Utc(now, nameof(now));
        Id = id;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; protected set; }
    public DateTimeOffset CreatedAtUtc { get; protected set; }
    public DateTimeOffset UpdatedAtUtc { get; protected set; }

    public void RestoreImportedTimestamps(DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Guard.Utc(createdAtUtc, nameof(createdAtUtc));
        Guard.Utc(updatedAtUtc, nameof(updatedAtUtc));
        if (updatedAtUtc < createdAtUtc) throw new DomainValidationException("UpdatedAtUtc must not be earlier than CreatedAtUtc.");
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }
}

internal static class Guard
{
    public static string Text(string value, int max, string name)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 || normalized.Length > max)
            throw new DomainValidationException($"{name} must contain 1 to {max} characters.");
        return normalized;
    }

    public static string? Optional(string? value, int max, string name)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        if (normalized.Length > max) throw new DomainValidationException($"{name} must be at most {max} characters.");
        return normalized.Length == 0 ? null : normalized;
    }

    public static void Utc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new DomainValidationException($"{name} must be UTC.");
    }
}

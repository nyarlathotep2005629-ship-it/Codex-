namespace WorkoutTracker.Domain;

public static class WeightValue
{
    public static long ToHundredthsKg(decimal kilograms)
    {
        ValidateOptional(kilograms);
        return checked((long)(kilograms * 100m));
    }
    public static decimal FromHundredthsKg(long value)
    {
        if (value is < 1 or > 100_000) throw new DomainValidationException("Persisted weight is outside 0.01 to 1000.00 kg.");
        return value / 100m;
    }
    public static void ValidateOptional(decimal? value)
    {
        if (value is null) return;
        if (value < 0.01m || value > 1000m || decimal.Truncate(value.Value * 100m) != value.Value * 100m)
            throw new DomainValidationException("Weight must be 0.01 to 1000.00 kg with at most two decimals.");
    }
}

public readonly record struct CompletedSet(decimal WeightKg, int Reps);
public readonly record struct RecordedSet(decimal? WeightKg, int? Reps);
public readonly record struct WorkoutOrderKey(DateOnly WorkoutDate, DateTimeOffset CompletedAtUtc, Guid Id);

public static class WorkoutCalculations
{
    public static decimal SetVolume(CompletedSet set) => set.WeightKg * set.Reps;
    public static decimal TotalVolume(IEnumerable<CompletedSet> sets) => sets.Sum(SetVolume);
    public static decimal TotalVolume(IEnumerable<RecordedSet> sets) => sets.Where(x => x.WeightKg.HasValue && x.Reps.HasValue).Sum(x => x.WeightKg!.Value * x.Reps!.Value);
    public static decimal? EpleyOneRepMax(decimal weightKg, int reps) => reps is < 1 or > 15 ? null : weightKg * (1m + reps / 30m);
    public static bool IsWeightPr(decimal weightKg, IEnumerable<CompletedSet> previous) => !previous.Any() || weightKg > previous.Max(x => x.WeightKg);
    public static bool IsEstimatedOneRepMaxPr(CompletedSet candidate, IEnumerable<CompletedSet> previous)
    {
        var candidateValue = EpleyOneRepMax(candidate.WeightKg, candidate.Reps);
        var previousValues = previous.Select(x => EpleyOneRepMax(x.WeightKg, x.Reps)).Where(x => x.HasValue).Select(x => x!.Value);
        return candidateValue.HasValue && (!previousValues.Any() || candidateValue.Value > previousValues.Max());
    }
    public static bool IsRepPr(CompletedSet candidate, IEnumerable<CompletedSet> previous) =>
        !previous.Any(x => x.WeightKg == candidate.WeightKg) || candidate.Reps > previous.Where(x => x.WeightKg == candidate.WeightKg).Max(x => x.Reps);
    public static IReadOnlyDictionary<Guid, decimal> PrimaryMuscleVolume(IEnumerable<(Guid PrimaryMuscleIdSnapshot, decimal Volume)> exercises) =>
        exercises.GroupBy(x => x.PrimaryMuscleIdSnapshot).ToDictionary(x => x.Key, x => x.Sum(y => y.Volume));
    public static bool IsBefore(WorkoutOrderKey possibleBaseline, WorkoutOrderKey current)
    {
        var date = possibleBaseline.WorkoutDate.CompareTo(current.WorkoutDate); if (date != 0) return date < 0;
        var completed = possibleBaseline.CompletedAtUtc.CompareTo(current.CompletedAtUtc); if (completed != 0) return completed < 0;
        return possibleBaseline.Id.CompareTo(current.Id) < 0;
    }
}

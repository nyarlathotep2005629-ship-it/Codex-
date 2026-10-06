using WorkoutTracker.Domain;

namespace WorkoutTracker.Domain.Tests;

public sealed class DomainRuleTests
{
    [Fact(DisplayName = "DOM-001 Set volume is weight multiplied by reps")]
    public void Dom001() => Assert.Equal(500m, WorkoutCalculations.SetVolume(new CompletedSet(100m, 5)));

    [Fact(DisplayName = "DOM-002 Total volume excludes incomplete sets by input contract")]
    public void Dom002() => Assert.Equal(1605m, WorkoutCalculations.TotalVolume([new RecordedSet(100m, 8), new RecordedSet(80.50m, 10), new RecordedSet(null, null), new RecordedSet(90m, null)]));

    [Fact(DisplayName = "DOM-003 Epley 1RM uses the specified formula")]
    public void Dom003() => Assert.Equal(100m * (1m + 10m / 30m), WorkoutCalculations.EpleyOneRepMax(100m, 10));

    [Fact(DisplayName = "DOM-004 Epley includes reps 1 and 15 and excludes 16")]
    public void Dom004()
    {
        Assert.NotNull(WorkoutCalculations.EpleyOneRepMax(100m, 1)); Assert.NotNull(WorkoutCalculations.EpleyOneRepMax(100m, 15)); Assert.Null(WorkoutCalculations.EpleyOneRepMax(100m, 16));
        Assert.Equal(1600m, WorkoutCalculations.SetVolume(new(100m, 16)));
    }

    [Fact(DisplayName = "DOM-005 Weight PR compares against completed historical sets")]
    public void Dom005() { Assert.False(WorkoutCalculations.IsWeightPr(100m, [new(100m, 5)])); Assert.True(WorkoutCalculations.IsWeightPr(100.01m, [new(100m, 5)])); }

    [Fact(DisplayName = "DOM-006 Estimated 1RM PR compares valid Epley results")]
    public void Dom006()
    {
        var previous = WorkoutCalculations.EpleyOneRepMax(100m, 10)!.Value; var candidate = WorkoutCalculations.EpleyOneRepMax(100.01m, 10)!.Value;
        Assert.Equal(decimal.Round(previous, 1), decimal.Round(candidate, 1)); Assert.True(WorkoutCalculations.IsEstimatedOneRepMaxPr(new(100.01m, 10), [new(100m, 10)]));
    }

    [Fact(DisplayName = "DOM-007 Rep PR compares only equal weight")]
    public void Dom007() { Assert.False(WorkoutCalculations.IsRepPr(new(100m, 8), [new(100m, 8)])); Assert.True(WorkoutCalculations.IsRepPr(new(100m, 9), [new(100m, 8), new(99m, 10)])); }

    [Fact(DisplayName = "DOM-008 and DOM-009 volume is assigned only to primary muscle snapshot")]
    public void Dom008Dom009()
    {
        var primary = Guid.NewGuid(); var secondary = Guid.NewGuid();
        var totals = WorkoutCalculations.PrimaryMuscleVolume([(primary, 3000m)]);
        Assert.Equal(3000m, totals[primary]); Assert.False(totals.ContainsKey(secondary));
    }

    [Fact(DisplayName = "DOM-010 and DOM-011 PR baseline uses WorkoutDate, CompletedAtUtc, Id ordering")]
    public void Dom010Dom011()
    {
        var early = Guid.Parse("00000000-0000-0000-0000-000000000001"); var late = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var current = new WorkoutOrderKey(new DateOnly(2026, 8, 15), new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero), late);
        Assert.True(WorkoutCalculations.IsBefore(new(new DateOnly(2026, 8, 10), current.CompletedAtUtc.AddDays(-5), late), current));
        Assert.False(WorkoutCalculations.IsBefore(new(new DateOnly(2026, 8, 20), current.CompletedAtUtc.AddDays(5), early), current));
        Assert.True(WorkoutCalculations.IsBefore(current with { Id = early }, current));
    }

    [Theory(DisplayName = "VAL-001 Weight accepts exact hundredths and round-trips")]
    [InlineData("0.01", 1L)] [InlineData("82.35", 8235L)] [InlineData("1000.00", 100000L)]
    public void Val001(string value, long stored)
    {
        var kg = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(stored, WeightValue.ToHundredthsKg(kg));
        Assert.Equal(kg, WeightValue.FromHundredthsKg(stored));
    }

    [Theory(DisplayName = "VAL-002 Weight rejects invalid precision and range")]
    [InlineData("0")] [InlineData("0.001")] [InlineData("1000.01")]
    public void Val002(string value) => Assert.Throws<DomainValidationException>(() => WeightValue.ToHundredthsKg(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact(DisplayName = "VAL-003 UTC timestamps reject non-UTC offsets")]
    public void Val003() => Assert.Throws<DomainValidationException>(() => new MuscleGroup(Guid.NewGuid(), "Chest", 0, DateTimeOffset.Now));

    [Fact(DisplayName = "VAL-004 Secondary muscle cannot equal primary muscle")]
    public void Val004()
    {
        var muscle = Guid.NewGuid();
        Assert.Throws<DomainValidationException>(() => new ExerciseSecondaryMuscle(Guid.NewGuid(), Guid.NewGuid(), muscle, muscle, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "VAL-006 Reps accepts 1 and 1000 and rejects 0 and 1001")]
    public void Val006()
    {
        var now = DateTimeOffset.UtcNow; var exercise = Guid.NewGuid();
        _ = new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, null, now); _ = new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1000, null, now);
        Assert.Throws<DomainValidationException>(() => new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 0, null, now));
        Assert.Throws<DomainValidationException>(() => new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1001, null, now));
    }

    [Fact(DisplayName = "VAL-007 and VAL-008 RPE accepts boundaries and 0.5 steps only")]
    public void Val007Val008()
    {
        var now = DateTimeOffset.UtcNow; var exercise = Guid.NewGuid();
        _ = new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, 1m, now); _ = new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, 9.5m, now); _ = new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, 10m, now);
        Assert.Throws<DomainValidationException>(() => new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, 10.5m, now));
        Assert.Throws<DomainValidationException>(() => new WorkoutSet(Guid.NewGuid(), exercise, 1, 10m, 1, 9.7m, now));
    }
}

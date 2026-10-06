using WorkoutTracker.Domain;

namespace WorkoutTracker.Application;

/// <summary>
/// Creates new rows for an already active Draft without changing its existing rows.
/// Persistence remains the responsibility of the existing AutoSaveCoordinator.
/// </summary>
public sealed record ActiveWorkoutExerciseAddition(
    ExerciseMasterDto Exercise,
    int PlannedSetCount,
    string? Note,
    PreviousPerformanceDto? PreviousPerformance);

public static class ActiveWorkoutDraftComposer
{
    public static ActiveWorkoutDto AddMissing(
        ActiveWorkoutDto workout,
        IEnumerable<ActiveWorkoutExerciseAddition> additions)
    {
        ArgumentNullException.ThrowIfNull(workout);
        ArgumentNullException.ThrowIfNull(additions);

        var exercises = workout.Exercises.OrderBy(x => x.DisplayOrder).ToList();
        var existingExerciseIds = exercises.Select(x => x.ExerciseId).ToHashSet();
        foreach (var addition in additions)
        {
            if (existingExerciseIds.Contains(addition.Exercise.Id)) continue;
            if (exercises.Count >= 50) throw new DomainValidationException("WorkoutのExerciseは50件までです。");
            if (addition.PlannedSetCount is < 1 or > 100) throw new DomainValidationException("1 ExerciseのSet数は1から100です。");

            var sets = Enumerable.Range(1, addition.PlannedSetCount).Select(setNumber =>
            {
                var previous = addition.PreviousPerformance?.Sets.SingleOrDefault(x => x.SetNumber == setNumber);
                return new ActiveWorkoutSetDto(Guid.NewGuid(), setNumber, null, null, null, previous?.WeightKg, previous?.Reps);
            }).ToArray();
            exercises.Add(new ActiveWorkoutExerciseDto(
                Guid.NewGuid(),
                addition.Exercise.Id,
                addition.Exercise.Name,
                addition.Exercise.PrimaryMuscleId,
                addition.Exercise.PrimaryMuscleName,
                exercises.Count,
                addition.Note,
                addition.PreviousPerformance,
                sets));
            existingExerciseIds.Add(addition.Exercise.Id);
        }

        return workout with { Exercises = exercises };
    }
}

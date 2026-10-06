namespace WorkoutTracker.Domain;

public sealed class MuscleGroup : EntityBase
{
    private MuscleGroup() { }
    public MuscleGroup(Guid id, string name, int displayOrder, DateTimeOffset now) : base(id, now)
    {
        Name = Guard.Text(name, 50, nameof(name));
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        DisplayOrder = displayOrder;
    }
    public string Name { get; private set; } = null!;
    public int DisplayOrder { get; private set; }
    public void Update(string name, int displayOrder, DateTimeOffset now)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        Guard.Utc(now, nameof(now)); Name = Guard.Text(name, 50, nameof(name)); DisplayOrder = displayOrder; UpdatedAtUtc = now;
    }

    public void ApplyImported(string name, int displayOrder, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Update(name, displayOrder, updatedAtUtc);
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class Exercise : EntityBase
{
    private Exercise() { }
    public Exercise(Guid id, string name, Guid primaryMuscleId, string? note, DateTimeOffset now) : base(id, now)
    {
        Name = Guard.Text(name, 100, nameof(name));
        if (primaryMuscleId == Guid.Empty) throw new DomainValidationException("PrimaryMuscleId must not be empty.");
        PrimaryMuscleId = primaryMuscleId;
        Note = Guard.Optional(note, 2000, nameof(note));
        IsActive = true;
    }
    public string Name { get; private set; } = null!;
    public Guid PrimaryMuscleId { get; private set; }
    public MuscleGroup PrimaryMuscle { get; private set; } = null!;
    public string? Note { get; private set; }
    public bool IsActive { get; private set; }
    public void Update(string name, Guid primaryMuscleId, string? note, bool isActive, DateTimeOffset now)
    {
        if (primaryMuscleId == Guid.Empty) throw new DomainValidationException("PrimaryMuscleId must not be empty.");
        Guard.Utc(now, nameof(now)); Name = Guard.Text(name, 100, nameof(name)); PrimaryMuscleId = primaryMuscleId;
        Note = Guard.Optional(note, 2000, nameof(note)); IsActive = isActive; UpdatedAtUtc = now;
    }

    public void ApplyImported(string name, Guid primaryMuscleId, string? note, bool isActive, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Update(name, primaryMuscleId, note, isActive, updatedAtUtc);
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class ExerciseSecondaryMuscle : EntityBase
{
    private ExerciseSecondaryMuscle() { }
    public ExerciseSecondaryMuscle(Guid id, Guid exerciseId, Guid muscleGroupId, Guid primaryMuscleId, DateTimeOffset now) : base(id, now)
    {
        if (exerciseId == Guid.Empty || muscleGroupId == Guid.Empty) throw new DomainValidationException("Foreign keys must not be empty.");
        if (muscleGroupId == primaryMuscleId) throw new DomainValidationException("Secondary muscle must differ from primary muscle.");
        ExerciseId = exerciseId;
        MuscleGroupId = muscleGroupId;
    }
    public Guid ExerciseId { get; private set; }
    public Exercise Exercise { get; private set; } = null!;
    public Guid MuscleGroupId { get; private set; }
    public MuscleGroup MuscleGroup { get; private set; } = null!;
}

public sealed class WorkoutMenu : EntityBase
{
    private readonly List<MenuExercise> _exercises = [];
    private WorkoutMenu() { }
    public WorkoutMenu(Guid id, string name, string? description, DateTimeOffset now) : base(id, now)
    {
        Name = Guard.Text(name, 100, nameof(name));
        Description = Guard.Optional(description, 2000, nameof(description));
    }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public IReadOnlyCollection<MenuExercise> Exercises => _exercises;
    public void Update(string name, string? description, DateTimeOffset now)
    {
        Guard.Utc(now, nameof(now)); Name = Guard.Text(name, 100, nameof(name));
        Description = Guard.Optional(description, 2000, nameof(description)); UpdatedAtUtc = now;
    }

    public void ApplyImported(string name, string? description, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Update(name, description, updatedAtUtc);
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class MenuExercise : EntityBase
{
    private MenuExercise() { }
    public MenuExercise(Guid id, Guid workoutMenuId, Guid exerciseId, int displayOrder, int defaultSetCount, string? note, DateTimeOffset now) : base(id, now)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        if (defaultSetCount is < 1 or > 20) throw new DomainValidationException("DefaultSetCount must be between 1 and 20.");
        WorkoutMenuId = workoutMenuId; ExerciseId = exerciseId; DisplayOrder = displayOrder; DefaultSetCount = defaultSetCount;
        Note = Guard.Optional(note, 1000, nameof(note));
    }
    public Guid WorkoutMenuId { get; private set; }
    public WorkoutMenu WorkoutMenu { get; private set; } = null!;
    public Guid ExerciseId { get; private set; }
    public Exercise Exercise { get; private set; } = null!;
    public int DisplayOrder { get; private set; }
    public int DefaultSetCount { get; private set; }
    public string? Note { get; private set; }
    public void Update(int displayOrder, int defaultSetCount, string? note, DateTimeOffset now)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        if (defaultSetCount is < 1 or > 20) throw new DomainValidationException("DefaultSetCount must be between 1 and 20.");
        Guard.Utc(now, nameof(now)); DisplayOrder = displayOrder; DefaultSetCount = defaultSetCount;
        Note = Guard.Optional(note, 1000, nameof(note)); UpdatedAtUtc = now;
    }

    public void ApplyImported(int displayOrder, int defaultSetCount, string? note, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Update(displayOrder, defaultSetCount, note, updatedAtUtc);
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class WorkoutSession : EntityBase
{
    private readonly List<WorkoutExercise> _exercises = [];
    private WorkoutSession() { }
    public WorkoutSession(Guid id, DateOnly workoutDate, Guid? sourceMenuId, string? name, string? note, DateTimeOffset now) : base(id, now)
    {
        WorkoutDate = workoutDate; SourceMenuId = sourceMenuId;
        Name = Guard.Optional(name, 100, nameof(name)); Note = Guard.Optional(note, 4000, nameof(note));
        Status = WorkoutStatus.Draft;
    }
    public Guid? SourceMenuId { get; private set; }
    public WorkoutMenu? SourceMenu { get; private set; }
    public string? Name { get; private set; }
    public DateOnly WorkoutDate { get; private set; }
    public WorkoutStatus Status { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public IReadOnlyCollection<WorkoutExercise> Exercises => _exercises;
    public void UpdateDraft(DateOnly workoutDate, string? name, string? note, DateTimeOffset now)
    {
        EnsureDraft(); Guard.Utc(now, nameof(now));
        WorkoutDate = workoutDate; Name = Guard.Optional(name, 100, nameof(name)); Note = Guard.Optional(note, 4000, nameof(note)); UpdatedAtUtc = now;
    }
    public void Complete(DateTimeOffset completedAtUtc)
    {
        Guard.Utc(completedAtUtc, nameof(completedAtUtc));
        Status = WorkoutStatus.Completed; CompletedAtUtc = completedAtUtc; UpdatedAtUtc = completedAtUtc;
    }
    private void EnsureDraft() { if (Status != WorkoutStatus.Draft) throw new DomainValidationException("Only a Draft workout can be edited."); }

    public void ApplyImportedCompleted(Guid? sourceMenuId, string? name, DateOnly workoutDate, string? note, DateTimeOffset completedAtUtc, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Guard.Utc(completedAtUtc, nameof(completedAtUtc));
        WorkoutDate = workoutDate;
        SourceMenuId = sourceMenuId;
        Name = Guard.Optional(name, 100, nameof(name));
        Note = Guard.Optional(note, 4000, nameof(note));
        Status = WorkoutStatus.Completed;
        CompletedAtUtc = completedAtUtc;
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class WorkoutExercise : EntityBase
{
    private readonly List<WorkoutSet> _sets = [];
    private WorkoutExercise() { }
    public WorkoutExercise(Guid id, Guid sessionId, Guid exerciseId, string exerciseNameSnapshot, Guid primaryMuscleIdSnapshot, string primaryMuscleNameSnapshot, int displayOrder, string? note, DateTimeOffset now) : base(id, now)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        WorkoutSessionId = sessionId; ExerciseId = exerciseId;
        ExerciseNameSnapshot = Guard.Text(exerciseNameSnapshot, 100, nameof(exerciseNameSnapshot));
        PrimaryMuscleIdSnapshot = primaryMuscleIdSnapshot;
        PrimaryMuscleNameSnapshot = Guard.Text(primaryMuscleNameSnapshot, 50, nameof(primaryMuscleNameSnapshot));
        DisplayOrder = displayOrder; Note = Guard.Optional(note, 2000, nameof(note));
    }
    public Guid WorkoutSessionId { get; private set; }
    public WorkoutSession WorkoutSession { get; private set; } = null!;
    public Guid ExerciseId { get; private set; }
    public Exercise Exercise { get; private set; } = null!;
    public string ExerciseNameSnapshot { get; private set; } = null!;
    public Guid PrimaryMuscleIdSnapshot { get; private set; }
    public string PrimaryMuscleNameSnapshot { get; private set; } = null!;
    public int DisplayOrder { get; private set; }
    public string? Note { get; private set; }
    public IReadOnlyCollection<WorkoutSet> Sets => _sets;
    public void UpdateDraft(int displayOrder, string? note, DateTimeOffset now)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        Guard.Utc(now, nameof(now)); DisplayOrder = displayOrder; Note = Guard.Optional(note, 2000, nameof(note)); UpdatedAtUtc = now;
    }

    public void ApplyImportedSnapshot(Guid exerciseId, string exerciseNameSnapshot, Guid primaryMuscleIdSnapshot, string primaryMuscleNameSnapshot, int displayOrder, string? note, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        if (displayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        if (exerciseId == Guid.Empty || primaryMuscleIdSnapshot == Guid.Empty) throw new DomainValidationException("Snapshot IDs must not be empty.");
        ExerciseId = exerciseId;
        ExerciseNameSnapshot = Guard.Text(exerciseNameSnapshot, 100, nameof(exerciseNameSnapshot));
        PrimaryMuscleIdSnapshot = primaryMuscleIdSnapshot;
        PrimaryMuscleNameSnapshot = Guard.Text(primaryMuscleNameSnapshot, 50, nameof(primaryMuscleNameSnapshot));
        DisplayOrder = displayOrder;
        Note = Guard.Optional(note, 2000, nameof(note));
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class WorkoutSet : EntityBase
{
    private WorkoutSet() { }
    public WorkoutSet(Guid id, Guid workoutExerciseId, int setNumber, decimal? weightKg, int? reps, decimal? rpe, DateTimeOffset now) : base(id, now)
    {
        if (setNumber < 1) throw new DomainValidationException("SetNumber must be positive.");
        WeightValue.ValidateOptional(weightKg);
        if (reps is < 1 or > 1000) throw new DomainValidationException("Reps must be between 1 and 1000.");
        if (rpe is not null && (rpe < 1m || rpe > 10m || decimal.Round(rpe.Value * 2m) != rpe.Value * 2m))
            throw new DomainValidationException("RPE must be 1 to 10 in 0.5 increments.");
        WorkoutExerciseId = workoutExerciseId; SetNumber = setNumber; WeightKg = weightKg; Reps = reps; Rpe = rpe;
    }
    public Guid WorkoutExerciseId { get; private set; }
    public WorkoutExercise WorkoutExercise { get; private set; } = null!;
    public int SetNumber { get; private set; }
    public decimal? WeightKg { get; private set; }
    public int? Reps { get; private set; }
    public decimal? Rpe { get; private set; }
    public bool IsEmpty => WeightKg is null && Reps is null && Rpe is null;
    public bool IsIncomplete => WeightKg.HasValue != Reps.HasValue;
    public bool IsCompletedSet => WeightKg.HasValue && Reps.HasValue;
    public void UpdateDraft(int setNumber, decimal? weightKg, int? reps, decimal? rpe, DateTimeOffset now)
    {
        if (setNumber < 1) throw new DomainValidationException("SetNumber must be positive.");
        WeightValue.ValidateOptional(weightKg);
        if (reps is < 1 or > 1000) throw new DomainValidationException("Reps must be between 1 and 1000.");
        if (rpe is not null && (rpe < 1m || rpe > 10m || decimal.Round(rpe.Value * 2m) != rpe.Value * 2m)) throw new DomainValidationException("RPE must be 1 to 10 in 0.5 increments.");
        Guard.Utc(now, nameof(now)); SetNumber = setNumber; WeightKg = weightKg; Reps = reps; Rpe = rpe; UpdatedAtUtc = now;
    }

    public void ApplyImported(int setNumber, decimal? weightKg, int? reps, decimal? rpe, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        UpdateDraft(setNumber, weightKg, reps, rpe, updatedAtUtc);
        RestoreImportedTimestamps(createdAtUtc, updatedAtUtc);
    }
}

public sealed class AppSettings
{
    private AppSettings() { }
    public int Id { get; private set; } = 1;
    public ThemeMode ThemeMode { get; private set; } = ThemeMode.System;
    public DashboardPeriod DashboardPeriod { get; private set; } = DashboardPeriod.ThreeMonths;
    public string DashboardExerciseIdsJson { get; private set; } = "[]";
    public double? WindowLeft { get; private set; }
    public double? WindowTop { get; private set; }
    public double? WindowWidth { get; private set; }
    public double? WindowHeight { get; private set; }
    public WindowDisplayState WindowState { get; private set; } = WindowDisplayState.Normal;
    public DateTimeOffset UpdatedAtUtc { get; private set; }
}

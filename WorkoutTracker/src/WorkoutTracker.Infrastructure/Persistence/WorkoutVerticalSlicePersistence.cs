using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class WorkoutVerticalSlicePersistence(IDbContextFactory<WorkoutTrackerDbContext> factory) : IWorkoutVerticalSlicePersistence
{
    private const int TemporaryOffset = 1_000_000;

    public async Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var existing = await db.WorkoutSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == WorkoutStatus.Draft, cancellationToken);
            if (existing is not null) return Existing(existing);

            WorkoutMenu? menu = null;
            if (request.MenuId.HasValue)
            {
                menu = await db.WorkoutMenus.Include(x => x.Exercises).ThenInclude(x => x.Exercise).ThenInclude(x => x.PrimaryMuscle).SingleOrDefaultAsync(x => x.Id == request.MenuId.Value, cancellationToken)
                    ?? throw new DomainValidationException("指定されたMenuが見つかりません。");
            }
            var session = new WorkoutSession(Guid.NewGuid(), request.WorkoutDate, menu?.Id, menu?.Name, null, now);
            db.WorkoutSessions.Add(session);
            var workoutExercises = new List<WorkoutExercise>(); var workoutSets = new List<WorkoutSet>();
            if (menu is not null)
            {
                foreach (var item in menu.Exercises.OrderBy(x => x.DisplayOrder).Select((value, index) => (value, index)))
                {
                    var exercise = item.value.Exercise;
                    var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, exercise.Id, exercise.Name, exercise.PrimaryMuscleId, exercise.PrimaryMuscle.Name, item.index, item.value.Note, now);
                    workoutExercises.Add(workoutExercise);
                    for (var setNumber = 1; setNumber <= item.value.DefaultSetCount; setNumber++) workoutSets.Add(new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, setNumber, null, null, null, now));
                }
            }
            db.AddRange(workoutExercises); db.AddRange(workoutSets);
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(false, Map(session, workoutExercises, workoutSets), null);
        }
        catch (DbUpdateException ex) when (IsDraftConflict(ex))
        {
            return await WaitForExistingDraftAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
        {
            return await WaitForExistingDraftAsync(cancellationToken);
        }
    }

    public async Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var session = await db.WorkoutSessions.AsNoTracking().Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).ThenInclude(x => x.Sets.OrderBy(s => s.SetNumber)).SingleOrDefaultAsync(x => x.Status == WorkoutStatus.Draft, cancellationToken);
        return session is null ? null : Map(session, session.Exercises, session.Exercises.SelectMany(x => x.Sets));
    }

    public async Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkoutExercises.AsNoTracking()
            .Where(x => x.ExerciseId == exerciseId && x.WorkoutSession.Status == WorkoutStatus.Completed)
            .OrderByDescending(x => x.WorkoutSession.WorkoutDate).ThenByDescending(x => x.WorkoutSession.CompletedAtUtc).ThenByDescending(x => x.WorkoutSession.Id)
            .Select(x => new { x.ExerciseId, x.WorkoutSession.WorkoutDate, Sets = x.Sets.OrderBy(s => s.SetNumber).Where(s => s.WeightKg.HasValue && s.Reps.HasValue).Select(s => new PreviousSetDto(s.SetNumber, s.WeightKg!.Value, s.Reps!.Value, s.Rpe)).ToList() })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new(row.ExerciseId, row.WorkoutDate, row.Sets);
    }

    public async Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidatePayload(request, now);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleOrDefaultAsync(x => x.Id == request.SessionId && x.Status == WorkoutStatus.Draft, cancellationToken)
            ?? throw new DomainValidationException("保存対象のDraftが見つかりません。");

        var exerciseIds = request.Exercises.Select(x => x.WorkoutExerciseId).ToHashSet();
        foreach (var removed in session.Exercises.Where(x => !exerciseIds.Contains(x.Id)).ToArray()) db.WorkoutExercises.Remove(removed);
        foreach (var exercise in session.Exercises.Where(x => exerciseIds.Contains(x.Id)))
        {
            var requestedSets = request.Exercises.Single(x => x.WorkoutExerciseId == exercise.Id).Sets.Select(x => x.WorkoutSetId).ToHashSet();
            foreach (var removed in exercise.Sets.Where(x => !requestedSets.Contains(x.Id)).ToArray()) db.WorkoutSets.Remove(removed);
        }
        await db.SaveChangesAsync(cancellationToken);
        await MoveOrderingToTemporaryRangeAsync(db, request.SessionId, cancellationToken);
        db.ChangeTracker.Clear();

        session = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleAsync(x => x.Id == request.SessionId && x.Status == WorkoutStatus.Draft, cancellationToken);
        session.UpdateDraft(request.WorkoutDate, request.WorkoutName, request.Note, now);
        var existingExercises = session.Exercises.ToDictionary(x => x.Id);
        foreach (var item in request.Exercises.OrderBy(x => x.DisplayOrder))
        {
            WorkoutExercise entity;
            if (existingExercises.TryGetValue(item.WorkoutExerciseId, out var existing)) entity = existing;
            else
            {
                var master = await db.Exercises.Include(x => x.PrimaryMuscle).SingleOrDefaultAsync(x => x.Id == item.ExerciseId && x.IsActive, cancellationToken)
                    ?? throw new DomainValidationException("追加対象のExerciseが見つかりません。");
                entity = new WorkoutExercise(item.WorkoutExerciseId, session.Id, master.Id, master.Name, master.PrimaryMuscleId, master.PrimaryMuscle.Name, item.DisplayOrder, item.Note, now);
                db.WorkoutExercises.Add(entity);
            }
            entity.UpdateDraft(item.DisplayOrder, item.Note, now);
            var existingSets = entity.Sets.ToDictionary(x => x.Id);
            foreach (var set in item.Sets.OrderBy(x => x.SetNumber))
            {
                if (existingSets.TryGetValue(set.WorkoutSetId, out var setEntity)) setEntity.UpdateDraft(set.SetNumber, set.WeightKg, set.Reps, set.Rpe, now);
                else db.WorkoutSets.Add(new WorkoutSet(set.WorkoutSetId, entity.Id, set.SetNumber, set.WeightKg, set.Reps, set.Rpe, now));
            }
        }
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleOrDefaultAsync(x => x.Id == sessionId && x.Status == WorkoutStatus.Draft, cancellationToken);
        if (session is null) return FinishWorkoutResult.Failure("Draftが見つかりません。");
        var errors = ValidateFinish(session);
        if (errors.Count != 0) return new(false, errors);

        foreach (var exercise in session.Exercises)
            foreach (var empty in exercise.Sets.Where(x => x.WeightKg is null && x.Reps is null && x.Rpe is null).ToArray()) db.WorkoutSets.Remove(empty);
        await db.SaveChangesAsync(cancellationToken);
        await MoveOrderingToTemporaryRangeAsync(db, sessionId, cancellationToken);
        db.ChangeTracker.Clear();
        session = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleAsync(x => x.Id == sessionId, cancellationToken);
        var order = 0;
        foreach (var exercise in session.Exercises.OrderBy(x => x.DisplayOrder))
        {
            exercise.UpdateDraft(order++, exercise.Note, now); var setNumber = 1;
            foreach (var set in exercise.Sets.OrderBy(x => x.SetNumber)) set.UpdateDraft(setNumber++, set.WeightKg, set.Reps, set.Rpe, now);
        }
        session.Complete(now);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return FinishWorkoutResult.Success();
    }

    public async Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var draft = await db.WorkoutSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.Status == WorkoutStatus.Draft, cancellationToken) ?? throw new DomainValidationException("Draftが見つかりません。");
        db.WorkoutSessions.Remove(draft); await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    private static List<string> ValidateFinish(WorkoutSession session)
    {
        var errors = new List<string>();
        if (session.Exercises.Count is < 1 or > 50) errors.Add("Exerciseを1件以上50件以下にしてください。");
        var sets = session.Exercises.SelectMany(x => x.Sets).ToArray();
        if (sets.Any(x => x.IsIncomplete || x.Rpe.HasValue && !x.IsCompletedSet)) errors.Add("WeightまたはRepsだけが入力された不完全なSetがあります。");
        if (!sets.Any(x => x.IsCompletedSet)) errors.Add("WeightとRepsを入力したSetが1件以上必要です。");
        return errors;
    }

    private static void ValidatePayload(SaveDraftRequest request, DateTimeOffset now)
    {
        if (request.Exercises.Count > 50) throw new DomainValidationException("WorkoutのExerciseは50件までです。");
        if (request.WorkoutDate < new DateOnly(1900, 1, 1) || request.WorkoutDate > DateOnly.FromDateTime(now.ToLocalTime().DateTime)) throw new DomainValidationException("WorkoutDateが範囲外です。");
        if (request.Exercises.Select(x => x.ExerciseId).Distinct().Count() != request.Exercises.Count) throw new DomainValidationException("同じExerciseを複数追加できません。");
        if (request.Exercises.Select(x => x.WorkoutExerciseId).Distinct().Count() != request.Exercises.Count) throw new DomainValidationException("WorkoutExerciseIdが重複しています。");
        if (!request.Exercises.OrderBy(x => x.DisplayOrder).Select(x => x.DisplayOrder).SequenceEqual(Enumerable.Range(0, request.Exercises.Count))) throw new DomainValidationException("DisplayOrderは0からの連番である必要があります。");
        var allSetIds = request.Exercises.SelectMany(x => x.Sets).Select(x => x.WorkoutSetId).ToArray();
        if (allSetIds.Distinct().Count() != allSetIds.Length) throw new DomainValidationException("WorkoutSetIdが重複しています。");
        foreach (var exercise in request.Exercises)
        {
            if (exercise.Sets.Count is < 1 or > 100) throw new DomainValidationException("1 ExerciseのSet数は1から100です。");
            if (!exercise.Sets.Select(x => x.SetNumber).SequenceEqual(Enumerable.Range(1, exercise.Sets.Count))) throw new DomainValidationException("SetNumberは1からの連番である必要があります。");
            foreach (var set in exercise.Sets) _ = new WorkoutSet(Guid.NewGuid(), Guid.NewGuid(), set.SetNumber, set.WeightKg, set.Reps, set.Rpe, now);
        }
    }

    private static async Task MoveOrderingToTemporaryRangeAsync(WorkoutTrackerDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorkoutExercises SET DisplayOrder = DisplayOrder + {TemporaryOffset} WHERE WorkoutSessionId = {sessionId}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorkoutSets SET SetNumber = SetNumber + {TemporaryOffset} WHERE WorkoutExerciseId IN (SELECT Id FROM WorkoutExercises WHERE WorkoutSessionId = {sessionId})", cancellationToken);
    }

    private async Task<StartPersistenceResult> WaitForExistingDraftAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var existing = await db.WorkoutSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == WorkoutStatus.Draft, cancellationToken);
            if (existing is not null) return Existing(existing);
            await Task.Delay(20, cancellationToken);
        }
        throw new InvalidOperationException("Draft start conflict could not be resolved.");
    }

    private static bool IsDraftConflict(DbUpdateException exception) => exception.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode == 19;
    private static StartPersistenceResult Existing(WorkoutSession session) => new(true, null, new(session.Id, session.WorkoutDate, session.Name));
    private static ActiveWorkoutDto Map(WorkoutSession session, IEnumerable<WorkoutExercise> exercises, IEnumerable<WorkoutSet> sets)
    {
        var setLookup = sets.ToLookup(x => x.WorkoutExerciseId);
        var exerciseDtos = exercises.OrderBy(x => x.DisplayOrder).Select(x => new ActiveWorkoutExerciseDto(x.Id, x.ExerciseId, x.ExerciseNameSnapshot, x.PrimaryMuscleIdSnapshot, x.PrimaryMuscleNameSnapshot, x.DisplayOrder, x.Note, null,
            setLookup[x.Id].OrderBy(s => s.SetNumber).Select(s => new ActiveWorkoutSetDto(s.Id, s.SetNumber, s.WeightKg, s.Reps, s.Rpe, null, null)).ToArray())).ToArray();
        return new(session.Id, session.SourceMenuId, session.Name, session.WorkoutDate, session.Note, exerciseDtos);
    }
}

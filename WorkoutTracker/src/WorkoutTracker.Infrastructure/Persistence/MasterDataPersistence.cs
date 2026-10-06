using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class MasterDataPersistence(IDbContextFactory<WorkoutTrackerDbContext> factory) : IMasterDataPersistence
{
    public async Task<IReadOnlyList<MuscleGroupDto>> GetMuscleGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MuscleGroups.AsNoTracking().OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .Select(x => new MuscleGroupDto(x.Id, x.Name, x.DisplayOrder, x.UpdatedAtUtc)).ToListAsync(ct);
    }

    public async Task<MuscleGroupDto> SaveMuscleGroupAsync(SaveMuscleGroupRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var name = ValidateText(request.Name, 50, nameof(request.Name));
        if (request.DisplayOrder < 0) throw new DomainValidationException("DisplayOrder must be non-negative.");
        if (await db.MuscleGroups.AnyAsync(x => x.Name == name && x.Id != request.Id, ct)) throw new DomainValidationException("同じ名前のMuscleGroupが存在します。");
        MuscleGroup entity;
        if (request.Id is { } id)
        {
            entity = await db.MuscleGroups.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("MuscleGroupが見つかりません。");
            entity.Update(name, request.DisplayOrder, now);
        }
        else
        {
            entity = new MuscleGroup(Guid.NewGuid(), name, request.DisplayOrder, now); db.MuscleGroups.Add(entity);
        }
        await db.SaveChangesAsync(ct);
        return new(entity.Id, entity.Name, entity.DisplayOrder, entity.UpdatedAtUtc);
    }

    public async Task ReorderMuscleGroupsAsync(IReadOnlyList<Guid> orderedIds, DateTimeOffset now, CancellationToken ct = default)
    {
        if (orderedIds.Count != orderedIds.Distinct().Count()) throw new DomainValidationException("MuscleGroup order contains duplicates.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.MuscleGroups.ToListAsync(ct);
        if (rows.Count != orderedIds.Count || rows.Select(x => x.Id).Except(orderedIds).Any()) throw new DomainValidationException("MuscleGroup order must contain every current group exactly once.");
        var lookup = rows.ToDictionary(x => x.Id);
        for (var i = 0; i < orderedIds.Count; i++) lookup[orderedIds[i]].Update(lookup[orderedIds[i]].Name, i, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task<MuscleGroupDeleteResult> DeleteMuscleGroupAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.MuscleGroups.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return MuscleGroupDeleteResult.NotFound;
        if (await db.Exercises.AnyAsync(x => x.PrimaryMuscleId == id, ct) || await db.ExerciseSecondaryMuscles.AnyAsync(x => x.MuscleGroupId == id, ct)) return MuscleGroupDeleteResult.Referenced;
        db.MuscleGroups.Remove(entity); await db.SaveChangesAsync(ct); return MuscleGroupDeleteResult.Deleted;
    }

    public async Task<IReadOnlyList<ExerciseMasterDto>> GetExercisesAsync(bool includeInactive, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Exercises.AsNoTracking().Include(x => x.PrimaryMuscle).AsQueryable();
        if (!includeInactive) query = query.Where(x => x.IsActive);
        var exercises = await query.OrderByDescending(x => x.IsActive).ThenBy(x => x.Name).ToListAsync(ct);
        var ids = exercises.Select(x => x.Id).ToList();
        var secondary = await db.ExerciseSecondaryMuscles.AsNoTracking().Where(x => ids.Contains(x.ExerciseId)).Include(x => x.MuscleGroup).ToListAsync(ct);
        var menuRefs = await db.MenuExercises.AsNoTracking().Where(x => ids.Contains(x.ExerciseId)).Select(x => x.ExerciseId).Distinct().ToListAsync(ct);
        var workoutRefs = await db.WorkoutExercises.AsNoTracking().Where(x => ids.Contains(x.ExerciseId)).Select(x => x.ExerciseId).Distinct().ToListAsync(ct);
        var referenced = menuRefs.Concat(workoutRefs).ToHashSet();
        return exercises.Select(x => MapExercise(x, secondary.Where(s => s.ExerciseId == x.Id), referenced.Contains(x.Id))).ToList();
    }

    public async Task<ExerciseMasterDto> SaveExerciseAsync(SaveExerciseRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        var name = ValidateText(request.Name, 100, nameof(request.Name));
        ValidateOptional(request.Note, 2000, nameof(request.Note));
        if (request.PrimaryMuscleId == Guid.Empty) throw new DomainValidationException("PrimaryMuscleId must not be empty.");
        if (request.SecondaryMuscleIds.Count != request.SecondaryMuscleIds.Distinct().Count()) throw new DomainValidationException("Secondary muscles must be unique.");
        if (request.SecondaryMuscleIds.Contains(request.PrimaryMuscleId)) throw new DomainValidationException("Secondary muscle must differ from primary muscle.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var muscleIds = request.SecondaryMuscleIds.Append(request.PrimaryMuscleId).Distinct().ToList();
        if (await db.MuscleGroups.CountAsync(x => muscleIds.Contains(x.Id), ct) != muscleIds.Count) throw new DomainValidationException("選択されたMuscleGroupが見つかりません。");
        if (await db.Exercises.AnyAsync(x => x.Name == name && x.Id != request.Id, ct)) throw new DomainValidationException("同じ名前のExerciseが存在します。");
        Exercise entity;
        if (request.Id is { } id)
        {
            entity = await db.Exercises.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Exerciseが見つかりません。");
            entity.Update(name, request.PrimaryMuscleId, request.Note, request.IsActive, now);
            var oldSecondary = await db.ExerciseSecondaryMuscles.Where(x => x.ExerciseId == id).ToListAsync(ct);
            db.ExerciseSecondaryMuscles.RemoveRange(oldSecondary);
        }
        else
        {
            entity = new Exercise(Guid.NewGuid(), name, request.PrimaryMuscleId, request.Note, now); db.Exercises.Add(entity);
        }
        foreach (var muscleId in request.SecondaryMuscleIds)
            db.ExerciseSecondaryMuscles.Add(new ExerciseSecondaryMuscle(Guid.NewGuid(), entity.Id, muscleId, request.PrimaryMuscleId, now));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return await LoadExerciseAsync(entity.Id, ct);
    }

    public async Task SetExerciseActiveAsync(Guid id, bool isActive, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Exercises.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Exerciseが見つかりません。");
        entity.Update(entity.Name, entity.PrimaryMuscleId, entity.Note, isActive, now); await db.SaveChangesAsync(ct);
    }

    public async Task<ExerciseDeleteResult> DeleteExerciseAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Exercises.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return ExerciseDeleteResult.NotFound;
        if (await db.MenuExercises.AnyAsync(x => x.ExerciseId == id, ct) || await db.WorkoutExercises.AnyAsync(x => x.ExerciseId == id, ct)) return ExerciseDeleteResult.ReferencedMustHide;
        db.Exercises.Remove(entity); await db.SaveChangesAsync(ct); return ExerciseDeleteResult.Deleted;
    }

    public async Task<IReadOnlyList<WorkoutMenuMasterDto>> GetMenusAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).ThenInclude(x => x.Exercise).OrderBy(x => x.Name).ToListAsync(ct);
        return rows.Select(MapMenu).ToList();
    }

    public async Task<WorkoutMenuMasterDto> SaveMenuAsync(SaveWorkoutMenuRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        var name = ValidateText(request.Name, 100, nameof(request.Name)); ValidateOptional(request.Description, 2000, nameof(request.Description)); ValidateMenuRows(request.Exercises);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exerciseIds = request.Exercises.Select(x => x.ExerciseId).ToList();
        if (await db.Exercises.CountAsync(x => exerciseIds.Contains(x.Id), ct) != exerciseIds.Count) throw new DomainValidationException("Menu内のExerciseが見つかりません。");
        WorkoutMenu menu;
        if (request.Id is { } id)
        {
            menu = await db.WorkoutMenus.Include(x => x.Exercises).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Menuが見つかりません。");
            menu.Update(name, request.Description, now);
            if (menu.Exercises.Count > 0)
            {
                var temporaryOffset = 1_000_000 + menu.Exercises.Max(x => x.DisplayOrder);
                foreach (var row in menu.Exercises) row.Update(row.DisplayOrder + temporaryOffset, row.DefaultSetCount, row.Note, now);
                await db.SaveChangesAsync(ct);
            }
            var requested = request.Exercises.Select(x => x.ExerciseId).ToHashSet();
            db.MenuExercises.RemoveRange(menu.Exercises.Where(x => !requested.Contains(x.ExerciseId)));
            for (var order = 0; order < request.Exercises.Count; order++)
            {
                var item = request.Exercises[order];
                var row = menu.Exercises.SingleOrDefault(x => x.ExerciseId == item.ExerciseId);
                if (row is null) db.MenuExercises.Add(new MenuExercise(Guid.NewGuid(), menu.Id, item.ExerciseId, order, item.DefaultSetCount, item.Note, now));
                else row.Update(order, item.DefaultSetCount, item.Note, now);
            }
        }
        else
        {
            menu = new WorkoutMenu(Guid.NewGuid(), name, request.Description, now); db.WorkoutMenus.Add(menu);
            for (var order = 0; order < request.Exercises.Count; order++)
            {
                var item = request.Exercises[order]; db.MenuExercises.Add(new MenuExercise(Guid.NewGuid(), menu.Id, item.ExerciseId, order, item.DefaultSetCount, item.Note, now));
            }
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return await LoadMenuAsync(menu.Id, ct);
    }

    public async Task<WorkoutMenuMasterDto> DuplicateMenuAsync(Guid id, string duplicateName, DateTimeOffset now, CancellationToken ct = default)
    {
        var name = ValidateText(duplicateName, 100, nameof(duplicateName));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Menuが見つかりません。");
        var copy = new WorkoutMenu(Guid.NewGuid(), name, source.Description, now); db.WorkoutMenus.Add(copy);
        foreach (var row in source.Exercises) db.MenuExercises.Add(new MenuExercise(Guid.NewGuid(), copy.Id, row.ExerciseId, row.DisplayOrder, row.DefaultSetCount, row.Note, now));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return await LoadMenuAsync(copy.Id, ct);
    }

    public async Task DeleteMenuAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var menu = await db.WorkoutMenus.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Menuが見つかりません。");
        db.WorkoutMenus.Remove(menu); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    private async Task<ExerciseMasterDto> LoadExerciseAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exercise = await db.Exercises.AsNoTracking().Include(x => x.PrimaryMuscle).SingleAsync(x => x.Id == id, ct);
        var secondary = await db.ExerciseSecondaryMuscles.AsNoTracking().Where(x => x.ExerciseId == id).Include(x => x.MuscleGroup).ToListAsync(ct);
        var referenced = await db.MenuExercises.AnyAsync(x => x.ExerciseId == id, ct) || await db.WorkoutExercises.AnyAsync(x => x.ExerciseId == id, ct);
        return MapExercise(exercise, secondary, referenced);
    }

    private async Task<WorkoutMenuMasterDto> LoadMenuAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return MapMenu(await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).ThenInclude(x => x.Exercise).SingleAsync(x => x.Id == id, ct));
    }

    private static ExerciseMasterDto MapExercise(Exercise x, IEnumerable<ExerciseSecondaryMuscle> secondary, bool referenced)
    {
        var rows = secondary.OrderBy(s => s.MuscleGroup.DisplayOrder).ThenBy(s => s.MuscleGroup.Name).ToList();
        return new(x.Id, x.Name, x.PrimaryMuscleId, x.PrimaryMuscle.Name, rows.Select(s => s.MuscleGroupId).ToList(), rows.Select(s => s.MuscleGroup.Name).ToList(), x.Note, x.IsActive, referenced, x.UpdatedAtUtc);
    }

    private static WorkoutMenuMasterDto MapMenu(WorkoutMenu menu) => new(menu.Id, menu.Name, menu.Description,
        menu.Exercises.OrderBy(x => x.DisplayOrder).Select(x => new MenuExerciseDto(x.Id, x.ExerciseId, x.Exercise.Name, x.DisplayOrder, x.DefaultSetCount, x.Note)).ToList(), menu.UpdatedAtUtc);

    private static void ValidateMenuRows(IReadOnlyList<SaveMenuExerciseRequest> rows)
    {
        if (rows.Select(x => x.ExerciseId).Distinct().Count() != rows.Count) throw new DomainValidationException("同じExerciseをMenuへ複数追加できません。");
        foreach (var row in rows)
        {
            if (row.ExerciseId == Guid.Empty) throw new DomainValidationException("ExerciseId must not be empty.");
            if (row.DefaultSetCount is < 1 or > 20) throw new DomainValidationException("DefaultSetCount must be between 1 and 20.");
            ValidateOptional(row.Note, 1000, nameof(row.Note));
        }
    }

    private static string ValidateText(string value, int max, string name)
    {
        var normalized = value.Trim(); if (normalized.Length is 0 || normalized.Length > max) throw new DomainValidationException($"{name} must contain 1 to {max} characters."); return normalized;
    }
    private static string? ValidateOptional(string? value, int max, string name)
    {
        if (value is not null && value.Trim().Length > max) throw new DomainValidationException($"{name} must be at most {max} characters."); return value;
    }
}

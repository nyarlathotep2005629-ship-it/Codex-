using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure;

public sealed class TransferValidationException(string message) : Exception(message);

internal static class TransferJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

internal sealed class TransferService(IDbContextFactory<WorkoutTrackerDbContext> factory) : ITransferService
{
    private const string Format = "WorkoutTrackerTransfer";
    private const int FormatVersion = 1;
    private const int DatabaseSchemaVersion = 1;
    private const long MaximumPackageBytes = 100 * 1024 * 1024;
    private const long MaximumEntryBytes = 75 * 1024 * 1024;

    public async Task<TransferFileResult> ExportAsync(string path, string sourcePlatform, string appVersion, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(sourcePlatform, "Windows", StringComparison.Ordinal) && !string.Equals(sourcePlatform, "Android", StringComparison.Ordinal))
            throw new ArgumentException("sourcePlatform must be Windows or Android.", nameof(sourcePlatform));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var muscles = await db.MuscleGroups.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var secondary = await db.ExerciseSecondaryMuscles.AsNoTracking().OrderBy(x => x.ExerciseId).ThenBy(x => x.MuscleGroupId).ToListAsync(cancellationToken);
        var secondaryByExercise = secondary.GroupBy(x => x.ExerciseId).ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.Select(y => y.MuscleGroupId).ToArray());
        var exercises = await db.Exercises.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var menus = await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var sessions = await db.WorkoutSessions.AsNoTracking().Where(x => x.Status == WorkoutStatus.Completed).Include(x => x.Exercises).ThenInclude(x => x.Sets).OrderBy(x => x.Id).ToListAsync(cancellationToken);

        var data = new TransferDataV1(
            muscles.Select(x => new MuscleGroupTransferDto(x.Id, x.Name, x.DisplayOrder, x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
            exercises.Select(x => new ExerciseTransferDto(x.Id, x.Name, x.PrimaryMuscleId, x.Note, x.IsActive, secondaryByExercise.GetValueOrDefault(x.Id, Array.Empty<Guid>()), x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
            menus.Select(x => new WorkoutMenuTransferDto(x.Id, x.Name, x.Description, x.Exercises.OrderBy(y => y.DisplayOrder).ThenBy(y => y.Id).Select(y => new MenuExerciseTransferDto(y.Id, y.WorkoutMenuId, y.ExerciseId, y.DisplayOrder, y.DefaultSetCount, y.Note, y.CreatedAtUtc, y.UpdatedAtUtc)).ToArray(), x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
            sessions.Select(x => new WorkoutSessionTransferDto(x.Id, x.SourceMenuId, x.Name, x.WorkoutDate, x.Status.ToString(), x.Note, x.CompletedAtUtc, x.Exercises.OrderBy(y => y.DisplayOrder).ThenBy(y => y.Id).Select(y => new WorkoutExerciseTransferDto(y.Id, y.WorkoutSessionId, y.ExerciseId, y.ExerciseNameSnapshot, y.PrimaryMuscleIdSnapshot, y.PrimaryMuscleNameSnapshot, y.DisplayOrder, y.Note, y.Sets.OrderBy(z => z.SetNumber).ThenBy(z => z.Id).Select(z => new WorkoutSetTransferDto(z.Id, z.SetNumber, z.WeightKg, z.Reps, z.Rpe, z.CreatedAtUtc, z.UpdatedAtUtc)).ToArray(), y.CreatedAtUtc, y.UpdatedAtUtc)).ToArray(), x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray());

        ValidateData(data);
        var counts = Count(data);
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(data, TransferJson.Options);
        var manifest = new TransferManifestV1(Format, FormatVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, sourcePlatform, appVersion, DatabaseSchemaVersion, "full", Convert.ToHexString(SHA256.HashData(dataBytes)), counts);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, TransferJson.Options);

        await using (var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var stream = manifestEntry.Open()) await stream.WriteAsync(manifestBytes, cancellationToken);
            var dataEntry = archive.CreateEntry("data.json", CompressionLevel.Optimal);
            await using (var stream = dataEntry.Open()) await stream.WriteAsync(dataBytes, cancellationToken);
        }
        return new TransferFileResult(fullPath, manifest);
    }

    public async Task<ImportPlan> CreateImportPlanAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await ReadPackageAsync(path, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var localMuscles = await db.MuscleGroups.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var localExercises = await db.Exercises.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var localSecondary = await db.ExerciseSecondaryMuscles.AsNoTracking().ToListAsync(cancellationToken);
        var localSecondaryByExercise = localSecondary.GroupBy(x => x.ExerciseId).ToDictionary(x => x.Key, x => x.Select(y => y.MuscleGroupId).OrderBy(y => y).ToArray());
        var localMenus = await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises).ToDictionaryAsync(x => x.Id, cancellationToken);
        var localSessions = await db.WorkoutSessions.AsNoTracking().Include(x => x.Exercises).ThenInclude(x => x.Sets).ToDictionaryAsync(x => x.Id, cancellationToken);

        var added = new List<TransferAggregateReference>();
        var unchanged = new List<TransferAggregateReference>();
        var conflicts = new List<TransferConflict>();
        foreach (var item in package.Data.MuscleGroups)
            Classify(item.Id, item.Name, localMuscles.ContainsKey(item.Id), localMuscles.TryGetValue(item.Id, out var localMuscle) && SameMuscle(localMuscle!, item), TransferAggregateKind.MuscleGroup, added, unchanged, conflicts);
        foreach (var item in package.Data.Exercises)
            Classify(item.Id, item.Name, localExercises.ContainsKey(item.Id), localExercises.TryGetValue(item.Id, out var localExercise) && SameExercise(localExercise!, item, localSecondaryByExercise.GetValueOrDefault(item.Id, [])), TransferAggregateKind.Exercise, added, unchanged, conflicts);
        foreach (var item in package.Data.WorkoutMenus)
            Classify(item.Id, item.Name, localMenus.ContainsKey(item.Id), localMenus.TryGetValue(item.Id, out var localMenu) && SameMenu(localMenu!, item), TransferAggregateKind.WorkoutMenu, added, unchanged, conflicts);
        foreach (var item in package.Data.WorkoutSessions)
            Classify(item.Id, item.Name ?? item.Id.ToString(), localSessions.ContainsKey(item.Id), localSessions.TryGetValue(item.Id, out var localSession) && SameSession(localSession!, item), TransferAggregateKind.WorkoutSession, added, unchanged, conflicts);

        return new ImportPlan(package, added, unchanged, conflicts, []);
    }

    public async Task<ImportResult> ImportAsync(ImportPlan plan, IReadOnlyList<ImportResolution> resolutions, CancellationToken cancellationToken = default)
    {
        var expected = plan.Conflicts.Select(x => (x.Kind, x.Id)).ToHashSet();
        var supplied = resolutions.Select(x => (x.Kind, x.Id)).ToHashSet();
        if (!expected.SetEquals(supplied)) throw new InvalidOperationException("All conflicts must have exactly one resolution before import.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var resolutionMap = resolutions.ToDictionary(x => (x.Kind, x.Id), x => x.UseImport);
        var added = 0; var updated = 0; var unchanged = plan.UnchangedItems.Count;

        var muscles = await db.MuscleGroups.ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var dto in plan.Package.Data.MuscleGroups)
        {
            var useImport = !muscles.ContainsKey(dto.Id) || resolutionMap.GetValueOrDefault((TransferAggregateKind.MuscleGroup, dto.Id), false);
            if (!useImport) continue;
            if (!muscles.TryGetValue(dto.Id, out var entity)) { entity = new MuscleGroup(dto.Id, dto.Name, dto.DisplayOrder, dto.CreatedAtUtc); entity.ApplyImported(dto.Name, dto.DisplayOrder, dto.CreatedAtUtc, dto.UpdatedAtUtc); db.MuscleGroups.Add(entity); muscles[dto.Id] = entity; added++; }
            else { entity.ApplyImported(dto.Name, dto.DisplayOrder, dto.CreatedAtUtc, dto.UpdatedAtUtc); updated++; }
        }

        var exercises = await db.Exercises.ToDictionaryAsync(x => x.Id, cancellationToken);
        var secondary = await db.ExerciseSecondaryMuscles.ToListAsync(cancellationToken);
        foreach (var dto in plan.Package.Data.Exercises)
        {
            var useImport = !exercises.ContainsKey(dto.Id) || resolutionMap.GetValueOrDefault((TransferAggregateKind.Exercise, dto.Id), false);
            if (!useImport) continue;
            if (!exercises.TryGetValue(dto.Id, out var entity)) { entity = new Exercise(dto.Id, dto.Name, dto.PrimaryMuscleId, dto.Note, dto.CreatedAtUtc); entity.ApplyImported(dto.Name, dto.PrimaryMuscleId, dto.Note, dto.IsActive, dto.CreatedAtUtc, dto.UpdatedAtUtc); db.Exercises.Add(entity); exercises[dto.Id] = entity; added++; }
            else { entity.ApplyImported(dto.Name, dto.PrimaryMuscleId, dto.Note, dto.IsActive, dto.CreatedAtUtc, dto.UpdatedAtUtc); updated++; }
            var desired = dto.SecondaryMuscleIds.ToHashSet();
            foreach (var link in secondary.Where(x => x.ExerciseId == dto.Id && !desired.Contains(x.MuscleGroupId)).ToArray()) db.ExerciseSecondaryMuscles.Remove(link);
            var present = secondary.Where(x => x.ExerciseId == dto.Id).Select(x => x.MuscleGroupId).ToHashSet();
            foreach (var muscleId in desired.Except(present)) db.ExerciseSecondaryMuscles.Add(new ExerciseSecondaryMuscle(Guid.NewGuid(), dto.Id, muscleId, dto.PrimaryMuscleId, dto.UpdatedAtUtc));
        }

        var menus = await db.WorkoutMenus.Include(x => x.Exercises).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var dto in plan.Package.Data.WorkoutMenus)
        {
            var useImport = !menus.ContainsKey(dto.Id) || resolutionMap.GetValueOrDefault((TransferAggregateKind.WorkoutMenu, dto.Id), false);
            if (!useImport) continue;
            if (!menus.TryGetValue(dto.Id, out var entity)) { entity = new WorkoutMenu(dto.Id, dto.Name, dto.Description, dto.CreatedAtUtc); entity.ApplyImported(dto.Name, dto.Description, dto.CreatedAtUtc, dto.UpdatedAtUtc); db.WorkoutMenus.Add(entity); menus[dto.Id] = entity; added++; }
            else { entity.ApplyImported(dto.Name, dto.Description, dto.CreatedAtUtc, dto.UpdatedAtUtc); updated++; }
            var wanted = dto.Exercises.Select(x => x.Id).ToHashSet();
            foreach (var child in entity.Exercises.Where(x => !wanted.Contains(x.Id)).ToArray()) db.MenuExercises.Remove(child);
            foreach (var child in dto.Exercises)
            {
                var current = entity.Exercises.SingleOrDefault(x => x.Id == child.Id);
                if (current is null) { current = new MenuExercise(child.Id, child.WorkoutMenuId, child.ExerciseId, child.DisplayOrder, child.DefaultSetCount, child.Note, child.CreatedAtUtc); current.ApplyImported(child.DisplayOrder, child.DefaultSetCount, child.Note, child.CreatedAtUtc, child.UpdatedAtUtc); db.MenuExercises.Add(current); }
                else current.ApplyImported(child.DisplayOrder, child.DefaultSetCount, child.Note, child.CreatedAtUtc, child.UpdatedAtUtc);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var sessions = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var dto in plan.Package.Data.WorkoutSessions)
        {
            var useImport = !sessions.ContainsKey(dto.Id) || resolutionMap.GetValueOrDefault((TransferAggregateKind.WorkoutSession, dto.Id), false);
            if (!useImport) continue;
            if (!Enum.TryParse<WorkoutStatus>(dto.Status, ignoreCase: false, out var status) || status != WorkoutStatus.Completed || dto.CompletedAtUtc is null) throw new TransferValidationException("Only completed WorkoutSession packages are supported.");
            if (!sessions.TryGetValue(dto.Id, out var entity)) { entity = new WorkoutSession(dto.Id, dto.WorkoutDate, dto.SourceMenuId, dto.Name, dto.Note, dto.CreatedAtUtc); entity.ApplyImportedCompleted(dto.SourceMenuId, dto.Name, dto.WorkoutDate, dto.Note, dto.CompletedAtUtc.Value, dto.CreatedAtUtc, dto.UpdatedAtUtc); db.WorkoutSessions.Add(entity); sessions[dto.Id] = entity; added++; }
            else { entity.ApplyImportedCompleted(dto.SourceMenuId, dto.Name, dto.WorkoutDate, dto.Note, dto.CompletedAtUtc.Value, dto.CreatedAtUtc, dto.UpdatedAtUtc); updated++; }
            var wanted = dto.Exercises.Select(x => x.Id).ToHashSet();
            foreach (var child in entity.Exercises.Where(x => !wanted.Contains(x.Id)).ToArray()) db.WorkoutExercises.Remove(child);
            foreach (var child in dto.Exercises)
            {
                var current = entity.Exercises.SingleOrDefault(x => x.Id == child.Id);
                if (current is null) { current = new WorkoutExercise(child.Id, child.WorkoutSessionId, child.ExerciseId, child.ExerciseNameSnapshot, child.PrimaryMuscleIdSnapshot, child.PrimaryMuscleNameSnapshot, child.DisplayOrder, child.Note, child.CreatedAtUtc); current.ApplyImportedSnapshot(child.ExerciseId, child.ExerciseNameSnapshot, child.PrimaryMuscleIdSnapshot, child.PrimaryMuscleNameSnapshot, child.DisplayOrder, child.Note, child.CreatedAtUtc, child.UpdatedAtUtc); db.WorkoutExercises.Add(current); }
                else current.ApplyImportedSnapshot(child.ExerciseId, child.ExerciseNameSnapshot, child.PrimaryMuscleIdSnapshot, child.PrimaryMuscleNameSnapshot, child.DisplayOrder, child.Note, child.CreatedAtUtc, child.UpdatedAtUtc);
                var wantedSets = child.Sets.Select(x => x.Id).ToHashSet();
                foreach (var set in current.Sets.Where(x => !wantedSets.Contains(x.Id)).ToArray()) db.WorkoutSets.Remove(set);
                foreach (var setDto in child.Sets)
                {
                    var set = current.Sets.SingleOrDefault(x => x.Id == setDto.Id);
                    if (set is null) { set = new WorkoutSet(setDto.Id, child.Id, setDto.SetNumber, setDto.WeightKg, setDto.Reps, setDto.Rpe, setDto.CreatedAtUtc); set.ApplyImported(setDto.SetNumber, setDto.WeightKg, setDto.Reps, setDto.Rpe, setDto.CreatedAtUtc, setDto.UpdatedAtUtc); db.WorkoutSets.Add(set); }
                    else set.ApplyImported(setDto.SetNumber, setDto.WeightKg, setDto.Reps, setDto.Rpe, setDto.CreatedAtUtc, setDto.UpdatedAtUtc);
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportResult(added, updated, unchanged, resolutions.Count(x => x.UseImport));
    }

    private static async Task<TransferPackage> ReadPackageAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Transfer package was not found.", fullPath);
        if (file.Length > MaximumPackageBytes) throw new TransferValidationException("Transfer package exceeds the maximum allowed size.");
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count != 2) throw new TransferValidationException("Transfer package must contain exactly manifest.json and data.json.");
        var entries = archive.Entries.ToDictionary(x => x.FullName, StringComparer.Ordinal);
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) || !entries.TryGetValue("data.json", out var dataEntry)) throw new TransferValidationException("Transfer package is missing manifest.json or data.json.");
        if (manifestEntry.Length > MaximumEntryBytes || dataEntry.Length > MaximumEntryBytes) throw new TransferValidationException("Transfer package entry exceeds the maximum allowed size.");
        foreach (var entry in archive.Entries) if (entry.FullName.Contains("..", StringComparison.Ordinal) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/')) throw new TransferValidationException("Transfer package contains an unsafe ZIP entry.");
        var manifestBytes = await ReadEntryAsync(manifestEntry, cancellationToken);
        var dataBytes = await ReadEntryAsync(dataEntry, cancellationToken);
        TransferManifestV1 manifest;
        TransferDataV1 data;
        try
        {
            manifest = JsonSerializer.Deserialize<TransferManifestV1>(manifestBytes, TransferJson.Options) ?? throw new TransferValidationException("manifest.json is invalid.");
            data = JsonSerializer.Deserialize<TransferDataV1>(dataBytes, TransferJson.Options) ?? throw new TransferValidationException("data.json is invalid.");
        }
        catch (JsonException ex)
        {
            throw new TransferValidationException($"Transfer JSON is invalid: {ex.Message}");
        }
        if (manifest.Format != Format || manifest.FormatVersion != FormatVersion || manifest.ExportType != "full" || manifest.PackageId == Guid.Empty) throw new TransferValidationException("Unsupported transfer manifest.");
        if (manifest.SourcePlatform is not ("Windows" or "Android")) throw new TransferValidationException("Unsupported source platform.");
        if (manifest.CreatedUtc.Offset != TimeSpan.Zero || manifest.DatabaseSchemaVersion != DatabaseSchemaVersion) throw new TransferValidationException("Unsupported transfer metadata.");
        if (!string.Equals(manifest.PayloadSha256, Convert.ToHexString(SHA256.HashData(dataBytes)), StringComparison.OrdinalIgnoreCase)) throw new TransferValidationException("Transfer payload SHA-256 does not match.");
        ValidateData(data);
        if (manifest.Counts != Count(data)) throw new TransferValidationException("Transfer manifest counts do not match data.json.");
        return new TransferPackage(manifest, data);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private static void ValidateData(TransferDataV1 data)
    {
        var muscles = Unique(data.MuscleGroups.Select(x => x.Id), "MuscleGroup");
        var exercises = Unique(data.Exercises.Select(x => x.Id), "Exercise");
        var menus = Unique(data.WorkoutMenus.Select(x => x.Id), "WorkoutMenu");
        var sessions = Unique(data.WorkoutSessions.Select(x => x.Id), "WorkoutSession");
        foreach (var x in data.MuscleGroups)
        {
            ValidateText(x.Name, 50, $"MuscleGroup {x.Id} Name");
            if (x.DisplayOrder < 0) throw new TransferValidationException($"MuscleGroup metadata is invalid: {x.Id}.");
            ValidateTimestamps(x.CreatedAtUtc, x.UpdatedAtUtc, $"MuscleGroup {x.Id}");
        }
        foreach (var x in data.Exercises)
        {
            if (!muscles.Contains(x.PrimaryMuscleId) || x.SecondaryMuscleIds.Count != x.SecondaryMuscleIds.Distinct().Count() || x.SecondaryMuscleIds.Any(id => !muscles.Contains(id) || id == x.PrimaryMuscleId)) throw new TransferValidationException($"Exercise relationship is invalid: {x.Id}.");
            ValidateText(x.Name, 100, $"Exercise {x.Id} Name");
            ValidateOptionalText(x.Note, 2000, $"Exercise {x.Id} Note");
            ValidateTimestamps(x.CreatedAtUtc, x.UpdatedAtUtc, $"Exercise {x.Id}");
        }
        var menuExerciseIds = new HashSet<Guid>();
        foreach (var menu in data.WorkoutMenus)
        {
            ValidateText(menu.Name, 100, $"WorkoutMenu {menu.Id} Name");
            ValidateOptionalText(menu.Description, 2000, $"WorkoutMenu {menu.Id} Description");
            ValidateTimestamps(menu.CreatedAtUtc, menu.UpdatedAtUtc, $"WorkoutMenu {menu.Id}");
            if (menu.Exercises.Select(x => x.Id).Distinct().Count() != menu.Exercises.Count || menu.Exercises.Any(x => x.Id == Guid.Empty || !menuExerciseIds.Add(x.Id) || x.WorkoutMenuId != menu.Id || !exercises.Contains(x.ExerciseId))) throw new TransferValidationException($"Menu relationship is invalid: {menu.Id}.");
            foreach (var child in menu.Exercises)
            {
                if (child.DisplayOrder < 0 || child.DefaultSetCount is < 1 or > 20) throw new TransferValidationException($"MenuExercise metadata is invalid: {child.Id}.");
                ValidateOptionalText(child.Note, 1000, $"MenuExercise {child.Id} Note");
                ValidateTimestamps(child.CreatedAtUtc, child.UpdatedAtUtc, $"MenuExercise {child.Id}");
            }
        }
        var workoutExerciseIds = new HashSet<Guid>();
        var workoutSetIds = new HashSet<Guid>();
        foreach (var session in data.WorkoutSessions)
        {
            if (!string.Equals(session.Status, WorkoutStatus.Completed.ToString(), StringComparison.Ordinal) || session.CompletedAtUtc is null || (session.SourceMenuId.HasValue && !menus.Contains(session.SourceMenuId.Value))) throw new TransferValidationException($"WorkoutSession metadata is invalid: {session.Id}.");
            ValidateOptionalText(session.Name, 100, $"WorkoutSession {session.Id} Name");
            ValidateOptionalText(session.Note, 4000, $"WorkoutSession {session.Id} Note");
            ValidateTimestamps(session.CreatedAtUtc, session.UpdatedAtUtc, $"WorkoutSession {session.Id}");
            ValidateUtc(session.CompletedAtUtc.Value, $"WorkoutSession {session.Id} CompletedAtUtc");
            if (session.Exercises.Select(x => x.Id).Distinct().Count() != session.Exercises.Count || session.Exercises.Any(x => x.Id == Guid.Empty || !workoutExerciseIds.Add(x.Id) || x.WorkoutSessionId != session.Id || !exercises.Contains(x.ExerciseId) || x.PrimaryMuscleIdSnapshot == Guid.Empty || !muscles.Contains(x.PrimaryMuscleIdSnapshot))) throw new TransferValidationException($"WorkoutExercise relationship is invalid: {session.Id}.");
            foreach (var exercise in session.Exercises)
            {
                ValidateText(exercise.ExerciseNameSnapshot, 100, $"WorkoutExercise {exercise.Id} ExerciseNameSnapshot");
                ValidateText(exercise.PrimaryMuscleNameSnapshot, 50, $"WorkoutExercise {exercise.Id} PrimaryMuscleNameSnapshot");
                ValidateOptionalText(exercise.Note, 2000, $"WorkoutExercise {exercise.Id} Note");
                ValidateTimestamps(exercise.CreatedAtUtc, exercise.UpdatedAtUtc, $"WorkoutExercise {exercise.Id}");
                if (exercise.DisplayOrder < 0 || exercise.Sets.Select(x => x.Id).Distinct().Count() != exercise.Sets.Count || exercise.Sets.Any(x => x.Id == Guid.Empty || !workoutSetIds.Add(x.Id)) || exercise.Sets.GroupBy(x => x.SetNumber).Any(g => g.Count() > 1)) throw new TransferValidationException($"WorkoutSet relationship is invalid: {exercise.Id}.");
                foreach (var set in exercise.Sets)
                {
                    if (set.SetNumber < 1 || set.Reps is < 1 or > 1000 || set.Rpe is < 1m or > 10m || (set.Rpe is not null && decimal.Round(set.Rpe.Value * 2m) != set.Rpe.Value * 2m) || (set.WeightKg is not null && (set.WeightKg < 0.01m || set.WeightKg > 1000m || decimal.Truncate(set.WeightKg.Value * 100m) != set.WeightKg.Value * 100m))) throw new TransferValidationException($"WorkoutSet metadata is invalid: {set.Id}.");
                    ValidateTimestamps(set.CreatedAtUtc, set.UpdatedAtUtc, $"WorkoutSet {set.Id}");
                }
            }
        }
        _ = sessions;
    }

    private static void ValidateText(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum) throw new TransferValidationException($"{label} is invalid.");
    }

    private static void ValidateOptionalText(string? value, int maximum, string label)
    {
        if (value is not null && value.Trim().Length > maximum) throw new TransferValidationException($"{label} is invalid.");
    }

    private static void ValidateTimestamps(DateTimeOffset created, DateTimeOffset updated, string label)
    {
        ValidateUtc(created, $"{label} CreatedAtUtc");
        ValidateUtc(updated, $"{label} UpdatedAtUtc");
        if (updated < created) throw new TransferValidationException($"{label} timestamps are invalid.");
    }

    private static void ValidateUtc(DateTimeOffset value, string label)
    {
        if (value.Offset != TimeSpan.Zero) throw new TransferValidationException($"{label} must be UTC.");
    }

    private static HashSet<Guid> Unique(IEnumerable<Guid> ids, string kind)
    {
        var array = ids.ToArray();
        if (array.Any(x => x == Guid.Empty) || array.Length != array.Distinct().Count()) throw new TransferValidationException($"Duplicate or empty {kind} Guid.");
        return array.ToHashSet();
    }

    private static TransferCounts Count(TransferDataV1 data) => new(data.MuscleGroups.Count, data.Exercises.Count, data.Exercises.Sum(x => x.SecondaryMuscleIds.Count), data.WorkoutMenus.Count, data.WorkoutMenus.Sum(x => x.Exercises.Count), data.WorkoutSessions.Count, data.WorkoutSessions.Sum(x => x.Exercises.Count), data.WorkoutSessions.Sum(x => x.Exercises.Sum(y => y.Sets.Count)));

    private static void Classify(Guid id, string label, bool exists, bool same, TransferAggregateKind kind, ICollection<TransferAggregateReference> added, ICollection<TransferAggregateReference> unchanged, ICollection<TransferConflict> conflicts)
    {
        if (!exists) added.Add(new(kind, id, label));
        else if (same) unchanged.Add(new(kind, id, label));
        else conflicts.Add(new(kind, id, label, "同一GuidのBusiness Dataが異なります。"));
    }

    private static bool SameMuscle(MuscleGroup local, MuscleGroupTransferDto dto) => local.Name == dto.Name && local.DisplayOrder == dto.DisplayOrder;
    private static bool SameExercise(Exercise local, ExerciseTransferDto dto, IEnumerable<Guid> secondary) => local.Name == dto.Name && local.PrimaryMuscleId == dto.PrimaryMuscleId && local.Note == dto.Note && local.IsActive == dto.IsActive && secondary.OrderBy(x => x).SequenceEqual(dto.SecondaryMuscleIds.OrderBy(x => x));
    private static bool SameMenu(WorkoutMenu local, WorkoutMenuTransferDto dto) => local.Name == dto.Name && local.Description == dto.Description && local.Exercises.OrderBy(x => x.Id).Select(ToBusiness).SequenceEqual(dto.Exercises.OrderBy(x => x.Id).Select(ToBusiness));
    private static bool SameSession(WorkoutSession local, WorkoutSessionTransferDto dto)
    {
        var localExercises = local.Exercises.OrderBy(x => x.Id).ToArray();
        var importedExercises = dto.Exercises.OrderBy(x => x.Id).ToArray();
        return local.Status.ToString() == dto.Status && local.SourceMenuId == dto.SourceMenuId && local.Name == dto.Name && local.WorkoutDate == dto.WorkoutDate && local.Note == dto.Note && local.CompletedAtUtc == dto.CompletedAtUtc && localExercises.Length == importedExercises.Length && localExercises.Zip(importedExercises).All(x => SameWorkoutExercise(x.First, x.Second));
    }
    private static bool SameWorkoutExercise(WorkoutExercise local, WorkoutExerciseTransferDto imported)
    {
        var localSets = local.Sets.OrderBy(x => x.Id).ToArray();
        var importedSets = imported.Sets.OrderBy(x => x.Id).ToArray();
        return local.Id == imported.Id && local.WorkoutSessionId == imported.WorkoutSessionId && local.ExerciseId == imported.ExerciseId && local.ExerciseNameSnapshot == imported.ExerciseNameSnapshot && local.PrimaryMuscleIdSnapshot == imported.PrimaryMuscleIdSnapshot && local.PrimaryMuscleNameSnapshot == imported.PrimaryMuscleNameSnapshot && local.DisplayOrder == imported.DisplayOrder && local.Note == imported.Note && localSets.Length == importedSets.Length && localSets.Zip(importedSets).All(x => x.First.Id == x.Second.Id && x.First.SetNumber == x.Second.SetNumber && x.First.WeightKg == x.Second.WeightKg && x.First.Reps == x.Second.Reps && x.First.Rpe == x.Second.Rpe);
    }
    private sealed record MenuExerciseBusiness(Guid Id, Guid WorkoutMenuId, Guid ExerciseId, int DisplayOrder, int DefaultSetCount, string? Note);
    private sealed record WorkoutSetBusiness(Guid Id, int SetNumber, decimal? WeightKg, int? Reps, decimal? Rpe);
    private sealed record WorkoutExerciseBusiness(Guid Id, Guid WorkoutSessionId, Guid ExerciseId, string ExerciseNameSnapshot, Guid PrimaryMuscleIdSnapshot, string PrimaryMuscleNameSnapshot, int DisplayOrder, string? Note, IReadOnlyList<WorkoutSetBusiness> Sets);
    private static MenuExerciseBusiness ToBusiness(MenuExercise x) => new(x.Id, x.WorkoutMenuId, x.ExerciseId, x.DisplayOrder, x.DefaultSetCount, x.Note);
    private static MenuExerciseBusiness ToBusiness(MenuExerciseTransferDto x) => new(x.Id, x.WorkoutMenuId, x.ExerciseId, x.DisplayOrder, x.DefaultSetCount, x.Note);
    private static WorkoutExerciseBusiness ToBusiness(WorkoutExercise x) => new(x.Id, x.WorkoutSessionId, x.ExerciseId, x.ExerciseNameSnapshot, x.PrimaryMuscleIdSnapshot, x.PrimaryMuscleNameSnapshot, x.DisplayOrder, x.Note, x.Sets.OrderBy(y => y.Id).Select(ToBusiness).ToArray());
    private static WorkoutExerciseBusiness ToBusiness(WorkoutExerciseTransferDto x) => new(x.Id, x.WorkoutSessionId, x.ExerciseId, x.ExerciseNameSnapshot, x.PrimaryMuscleIdSnapshot, x.PrimaryMuscleNameSnapshot, x.DisplayOrder, x.Note, x.Sets.OrderBy(y => y.Id).Select(ToBusiness).ToArray());
    private static WorkoutSetBusiness ToBusiness(WorkoutSet x) => new(x.Id, x.SetNumber, x.WeightKg, x.Reps, x.Rpe);
    private static WorkoutSetBusiness ToBusiness(WorkoutSetTransferDto x) => new(x.Id, x.SetNumber, x.WeightKg, x.Reps, x.Rpe);

}

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed record StandardCatalogSeedResult(int AddedMuscleGroups, int AddedExercises, int ReusedExercises, IReadOnlyList<string> Conflicts);

public interface IStandardCatalogSeeder
{
    Task<StandardCatalogSeedResult> SeedAsync(CancellationToken cancellationToken = default);
}

public static class StandardExerciseCatalog
{
    // The order and names are the approved v1.0 catalog. Keep aliases explicit and unambiguous.
    public static readonly IReadOnlyList<(string Muscle, string[] Exercises)> Groups =
    [
        ("胸", Split("ベンチプレス|インクラインベンチプレス|ダンベルベンチプレス|インクラインダンベルプレス|ダンベルフライ|チェストプレス|ペックデックフライ|ケーブルクロスオーバー|スミスマシンベンチプレス|プッシュアップ")),
        ("背中", Split("デッドリフト|ラットプルダウン|ワイドグリップラットプルダウン|クローズグリップラットプルダウン|チンニング（懸垂）|アシストチンニング|シーテッドロー|チェストサポートロー|ベントオーバーロー|ワンハンドダンベルロー|Tバーロー|ストレートアームプルダウン")),
        ("肩", Split("バーベルショルダープレス|ダンベルショルダープレス|マシンショルダープレス|アーノルドプレス|サイドレイズ|ケーブルサイドレイズ|フロントレイズ|リアレイズ|リバースペックデック|フェイスプル")),
        ("上腕二頭筋", Split("バーベルカール|EZバーカール|ダンベルカール|インクラインダンベルカール|プリーチャーカール|ケーブルカール|ハンマーカール|コンセントレーションカール")),
        ("上腕三頭筋", Split("クローズグリップベンチプレス|ライイングトライセプスエクステンション|オーバーヘッドダンベルエクステンション|ケーブルプレスダウン（ストレートバー）|ローププレスダウン|ワンハンドケーブルエクステンション|ダンベルキックバック|トライセプスディップ")),
        ("前腕", Split("リストカール|リバースリストカール|リストローラー|リストプロネーション|リストスピネーション")),
        ("大腿四頭筋", Split("バックスクワット|フロントスクワット|ゴブレットスクワット|スミスマシンスクワット|レッグプレス|ハックスクワット|レッグエクステンション|ブルガリアンスプリットスクワット|ランジ")),
        ("ハムストリングス", Split("ルーマニアンデッドリフト|スティッフレッグデッドリフト|シーテッドレッグカール|ライイングレッグカール|スタンディングレッグカール|ノルディックハムストリングカール|グッドモーニング|スイスボールレッグカール")),
        ("臀部", Split("ヒップスラスト|グルートブリッジ|ケーブルグルートキックバック|ヒップアブダクションマシン|シングルレッグヒップスラスト|ステップアップ|リバースランジ|ケーブルプルスルー")),
        ("下腿", Split("スタンディングカーフレイズ|シーテッドカーフレイズ|レッグプレスカーフレイズ|ドンキーカーフレイズ|シングルレッグカーフレイズ")),
        ("腹部", Split("クランチ|ケーブルクランチ|リバースクランチ|シットアップ|ハンギングレッグレイズ|キャプテンチェアニーアップ|アブローラー|プランク|サイドプランク|デッドバグ")),
        ("その他", Split("バーピー|マウンテンクライマー|ケトルベルスイング|パワークリーン|ハングクリーン|スナッチ|ターキッシュゲットアップ"))
    ];

    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Bench Press"] = "ベンチプレス",
        ["Barbell Bench Press"] = "ベンチプレス",
        ["Incline Bench Press"] = "インクラインベンチプレス",
        ["Dumbbell Bench Press"] = "ダンベルベンチプレス",
        ["Dumbbell Fly"] = "ダンベルフライ",
        ["Push Up"] = "プッシュアップ",
        ["Push-Up"] = "プッシュアップ",
        ["Deadlift"] = "デッドリフト",
        ["Lat Pulldown"] = "ラットプルダウン",
        ["Pull Up"] = "チンニング（懸垂）",
        ["Pull-Up"] = "チンニング（懸垂）",
        ["Chin Up"] = "チンニング（懸垂）",
        ["Back Squat"] = "バックスクワット",
        ["Barbell Squat"] = "バックスクワット",
        ["Front Squat"] = "フロントスクワット",
        ["Leg Press"] = "レッグプレス",
        ["Romanian Deadlift"] = "ルーマニアンデッドリフト",
        ["Hip Thrust"] = "ヒップスラスト",
        ["Plank"] = "プランク"
    };

    public static string CanonicalName(string name)
    {
        var trimmed = name.Trim();
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    public static Guid StableId(string kind, string canonicalName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"WorkoutTracker.v1.catalog:{kind}:{canonicalName}"));
        var id = bytes[..16];
        id[7] = (byte)((id[7] & 0x0f) | 0x50);
        id[8] = (byte)((id[8] & 0x3f) | 0x80);
        return new Guid(id);
    }

    private static string[] Split(string names) => names.Split('|');
}

internal sealed class StandardCatalogSeeder(IDbContextFactory<WorkoutTrackerDbContext> factory) : IStandardCatalogSeeder
{
    public async Task<StandardCatalogSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var muscles = await db.MuscleGroups.ToListAsync(cancellationToken);
        var exercises = await db.Exercises.ToListAsync(cancellationToken);
        var conflicts = new List<string>();
        var addedMuscles = 0;
        var addedExercises = 0;
        var reused = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var (muscleName, names) in StandardExerciseCatalog.Groups)
        {
            var muscle = muscles.SingleOrDefault(x => string.Equals(x.Name, muscleName, StringComparison.OrdinalIgnoreCase));
            if (muscle is null)
            {
                var id = StandardExerciseCatalog.StableId("muscle", muscleName);
                if (muscles.Any(x => x.Id == id)) { conflicts.Add($"部位IDの衝突: {muscleName}"); continue; }
                muscle = new MuscleGroup(id, muscleName, muscles.Count, now);
                db.MuscleGroups.Add(muscle);
                muscles.Add(muscle);
                addedMuscles++;
            }

            foreach (var name in names)
            {
                var id = StandardExerciseCatalog.StableId("exercise", name);
                var matchingNames = exercises.Where(x => string.Equals(StandardExerciseCatalog.CanonicalName(x.Name), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matchingNames.Length > 1)
                {
                    conflicts.Add($"同一種目に複数の既存IDがあります: {name}");
                    continue;
                }
                var existingById = exercises.SingleOrDefault(x => x.Id == id);
                if (existingById is not null)
                {
                    if (string.Equals(StandardExerciseCatalog.CanonicalName(existingById.Name), name, StringComparison.OrdinalIgnoreCase)) reused++;
                    else conflicts.Add($"種目IDの衝突: {name}");
                    continue;
                }
                var existingByName = matchingNames.SingleOrDefault();
                if (existingByName is not null) { reused++; continue; }
                var exercise = new Exercise(id, name, muscle.Id, null, now);
                db.Exercises.Add(exercise);
                exercises.Add(exercise);
                addedExercises++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(addedMuscles, addedExercises, reused, conflicts);
    }
}

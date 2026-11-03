namespace WorkoutTracker.Application;

/// <summary>Shared user-facing explanations for workout intensity metrics.</summary>
public static class WorkoutMetricExplanations
{
    public const string EstimatedOneRepMax = """
        RM（Repetition Maximum）は、決められた回数を正しいフォームで挙上できる最大重量を指します。1RMは1回だけ挙上できる最大重量です。

        推定1RMは、複数回挙上した重量から1回の最大重量を計算した目安です。実際に1回だけ挙上して測定した重量とは異なる場合があります。

        WorkoutTrackerではEpley式を使用します。
        推定1RM = 重量 × (1 + 回数 / 30)

        計算例：60 kg × 5回 → 60 × (1 + 5 / 30) を計算し、画面上は70 kgと表示します。
        回数が1回でも特別処理はせず、同じ式を適用します（60 kg × 1回は画面上62 kgと表示）。

        推定対象は1～15回です。回数が範囲外、重量または回数が未入力、あるいは有効な重量でない場合は計算できません。アプリの重量入力範囲は0.01～1000.00 kgです。

        Domainの計算式には明示的な丸め処理がありません。画面では各表示形式に合わせて桁数を整えます。RPEはEpley式の入力には使用しません。
        """;

    public const string Rpe = """
        RPE（Rate of Perceived Exertion）は、セットを行った本人が感じた主観的な運動強度です。WorkoutTrackerでは1～10を0.5刻みで入力します。

        一般的な目安（RIRは正しいフォームであと何回できそうかの回数です）：
        RPE 10：あと1回もできない（0 RIR）
        RPE 9：約1回できる（約1 RIR）
        RPE 8：約2回できる（約2 RIR）
        RPE 7：約3回できる（約3 RIR）

        RPEとRIRの対応は目安で、個人や種目、当日の状態によって変わります。RPEはアプリによる自動計算値ではなく、ユーザーが入力した記録です。RIRへの自動換算やRPEからの重量・1RM推定は行いません。
        """;
}

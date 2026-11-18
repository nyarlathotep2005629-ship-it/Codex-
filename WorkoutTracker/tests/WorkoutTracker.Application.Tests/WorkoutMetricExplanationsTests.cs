using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Application.Tests;

public sealed class WorkoutMetricExplanationsTests
{
    [Fact(DisplayName = "RM説明はEpley式・1～15回・1回の動作とDomain計算に一致する")]
    public void EstimatedOneRepMaxExplanationMatchesDomainRules()
    {
        var explanation = WorkoutMetricExplanations.EstimatedOneRepMax;

        Assert.Contains("重量 × (1 + 回数 / 30)", explanation);
        Assert.Contains("1～15回", explanation);
        Assert.Contains("1回でも特別処理はせず", explanation);
        Assert.Contains("60 × (1 + 5 / 30)", explanation);
        Assert.Contains("画面上は70 kg", explanation);
        Assert.Equal(70m, decimal.Round(WorkoutCalculations.EpleyOneRepMax(60m, 5)!.Value, 2));
        Assert.Equal(62m, decimal.Round(WorkoutCalculations.EpleyOneRepMax(60m, 1)!.Value, 2));
        Assert.Null(WorkoutCalculations.EpleyOneRepMax(60m, 0));
        Assert.Null(WorkoutCalculations.EpleyOneRepMax(60m, 16));
    }

    [Fact(DisplayName = "RPE説明は入力制約とRIRの概算関係を示し、自動換算しない")]
    public void RpeExplanationMatchesInputAndCalculationBehavior()
    {
        var explanation = WorkoutMetricExplanations.Rpe;

        Assert.Contains("主観的な運動強度", explanation);
        Assert.Contains("1～10を0.5刻み", explanation);
        Assert.Contains("RPE 10：あと1回もできない（0 RIR）", explanation);
        Assert.Contains("RPE 9：約1回できる", explanation);
        Assert.Contains("RPE 8：約2回できる", explanation);
        Assert.Contains("RPE 7：約3回できる", explanation);
        Assert.Contains("ユーザーが入力した記録", explanation);
        Assert.Contains("自動換算", explanation);
    }
}

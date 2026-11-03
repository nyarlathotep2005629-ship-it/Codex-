using WorkoutTracker.Mobile;

namespace WorkoutTracker.App.Tests;

public sealed class AsyncOperationGateTests
{
    [Fact(DisplayName = "統計の初期ロード中は手動更新を重ねず、処理終了後に再実行できる")]
    public async Task RejectsConcurrentRequestAndReleasesAfterCompletion()
    {
        var gate = new AsyncOperationGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.TryRunAsync(async () =>
        {
            started.SetResult();
            await release.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gate.IsRunning);
        Assert.False(await gate.TryRunAsync(() => Task.CompletedTask));
        release.SetResult();
        Assert.True(await first);
        Assert.False(gate.IsRunning);
        Assert.True(await gate.TryRunAsync(() => Task.CompletedTask));
    }

    [Fact(DisplayName = "統計取得失敗・キャンセル後もローディングゲートを解放する")]
    public async Task ReleasesAfterFailureAndCancellation()
    {
        var gate = new AsyncOperationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TryRunAsync(() => throw new InvalidOperationException()));
        Assert.False(gate.IsRunning);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.TryRunAsync(() => Task.FromCanceled(cancellation.Token)));
        Assert.False(gate.IsRunning);
        Assert.True(await gate.TryRunAsync(() => Task.CompletedTask));
    }
}

using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace WorkoutTracker.Mobile;

/// <summary>Storage Access Framework implementation. The user, not the app, selects the destination.</summary>
public sealed class AndroidTransferFileSaveService : ITransferFileSaveService
{
    private const int CreateDocumentRequestCode = 41827;
    private static TaskCompletionSource<Android.Net.Uri?>? _pendingResult;
    private static readonly object PendingLock = new();

    public async Task<TransferFileSaveResult> SaveAsync(string suggestedFileName, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(source);
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("ファイル保存画面を開けませんでした。");
        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (PendingLock)
        {
            if (_pendingResult is not null) throw new InvalidOperationException("別のファイル保存操作が進行中です。");
            _pendingResult = completion;
        }
        try
        {
            var intent = new Intent(Intent.ActionCreateDocument)
                .AddCategory(Intent.CategoryOpenable)
                .SetType("application/vnd.workouttracker.wtb")
                .PutExtra(Intent.ExtraTitle, suggestedFileName)
                .AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
            activity.StartActivityForResult(intent, CreateDocumentRequestCode);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var uri = await completion.Task;
            if (uri is null) return new(false, null);
            var resolver = Android.App.Application.Context?.ContentResolver ?? throw new InvalidOperationException("保存先へ接続できませんでした。");
            await using var output = resolver.OpenOutputStream(uri) ?? throw new IOException("保存先を開けませんでした。");
            await source.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            return new(true, uri.ToString());
        }
        finally
        {
            lock (PendingLock)
            {
                if (ReferenceEquals(_pendingResult, completion)) _pendingResult = null;
            }
        }
    }

    internal static bool TryCompleteCreateDocument(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != CreateDocumentRequestCode) return false;
        TaskCompletionSource<Android.Net.Uri?>? completion;
        lock (PendingLock) completion = _pendingResult;
        completion?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
        return true;
    }
}

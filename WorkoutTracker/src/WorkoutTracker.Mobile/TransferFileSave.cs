namespace WorkoutTracker.Mobile;

/// <summary>Platform boundary for user-mediated transfer-file creation.</summary>
public sealed record TransferFileSaveResult(bool Saved, string? Destination);

public interface ITransferFileSaveService
{
    Task<TransferFileSaveResult> SaveAsync(string suggestedFileName, Stream source, CancellationToken cancellationToken = default);
}

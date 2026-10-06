using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace WorkoutTracker.App.Services;

public interface IUiExceptionReporter
{
    void Report(Exception exception, string operation);
    void Report(Exception exception, string operation, Guid correlationId) =>
        Report(exception, $"{operation} (Correlation ID: {correlationId})");
}

public sealed class LocalFileUiExceptionReporter(string logDirectory) : IUiExceptionReporter
{
    private static readonly object WriteGate = new();

    public void Report(Exception exception, string operation) => Write(exception, operation, null);
    public void Report(Exception exception, string operation, Guid correlationId) => Write(exception, operation, correlationId);

    private void Write(Exception exception, string operation, Guid? correlationId)
    {
        var correlation = correlationId is null ? string.Empty : $" Correlation ID: {correlationId}";
        var entry = $"[{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}] {operation}.{correlation}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        Trace.TraceError("UI operation failed: {0}{1}{2}", operation, Environment.NewLine, exception);

        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, "ui-errors.log"), entry);
            }
        }
        catch (Exception logException)
        {
            Trace.TraceError("Failed to persist UI error log: {0}", logException);
        }
    }
}

public sealed class FatalErrorGate
{
    private int _entered;
    public bool TryEnter() => Interlocked.Exchange(ref _entered, 1) == 0;
}

internal sealed class NullUiExceptionReporter : IUiExceptionReporter
{
    public static NullUiExceptionReporter Instance { get; } = new();
    public void Report(Exception exception, string operation) { }
}

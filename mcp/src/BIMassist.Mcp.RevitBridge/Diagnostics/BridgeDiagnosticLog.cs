using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Diagnostics;

internal sealed class BridgeDiagnosticLog
{
    private const long MaximumLogBytes = 1_048_576;
    private static readonly object Sync = new();
    private readonly string _path;

    internal BridgeDiagnosticLog(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A diagnostic log path is required.", nameof(path))
            : Path.GetFullPath(path);
    }

    internal static BridgeDiagnosticLog CreateDefault()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return new BridgeDiagnosticLog(Path.Combine(localData, "BIMassist", "Mcp", $"bridge-diagnostics-{Environment.ProcessId}.log"));
    }

    internal void Write(BridgeRequest request, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        string stack = exception.StackTrace ?? "<stack unavailable>";
        string validationDetail = exception.Data["BIMassist.ElementValidationCode"] is string validationCode &&
                                 exception.Data["BIMassist.ElementId"] is long elementId
            ? $" elementValidation={validationCode} elementId={elementId}"
            : string.Empty;
        string record = $"{DateTimeOffset.UtcNow:O} requestId={request.RequestId} operation={request.Operation} exception={exceptionType}{validationDetail}{Environment.NewLine}{stack}{Environment.NewLine}";
        lock (Sync)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_path) && new FileInfo(_path).Length + System.Text.Encoding.UTF8.GetByteCount(record) > MaximumLogBytes)
            {
                string previousPath = _path + ".1";
                File.Move(_path, previousPath, overwrite: true);
            }

            File.AppendAllText(_path, record, System.Text.Encoding.UTF8);
        }
    }
}

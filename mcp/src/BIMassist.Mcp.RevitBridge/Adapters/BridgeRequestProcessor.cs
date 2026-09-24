using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;

namespace BIMassist.Mcp.RevitBridge.Adapters;

public interface IRevitContextSnapshotProvider
{
    SessionDescriptor GetSessionSnapshot();

    DocumentDescriptor? GetActiveDocument();
}

public sealed class BridgeRequestProcessor
{
    private readonly IRevitContextSnapshotProvider _context;

    public BridgeRequestProcessor(IRevitContextSnapshotProvider context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public BridgeResponse Process(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        long started = Stopwatch.GetTimestamp();

        BridgeResponse response = request.Operation switch
        {
            BridgeOperations.GetStatus => Success(
                request.RequestId,
                _context.GetSessionSnapshot(),
                documentRevision: null,
                started),
            BridgeOperations.GetDocumentContext => ProcessDocumentContext(request, started),
            _ => Failure(
                request.RequestId,
                BridgeErrorCodes.OperationNotSupported,
                "The requested operation is not available in the current bridge phase.",
                started)
        };

        ContractValidator.Validate(response);
        return response;
    }

    private BridgeResponse ProcessDocumentContext(BridgeRequest request, long started)
    {
        SessionDescriptor session = _context.GetSessionSnapshot();
        if (!string.Equals(request.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            return Failure(
                request.RequestId,
                BridgeErrorCodes.SessionNotFound,
                "The requested Revit session is not active.",
                started);
        }

        DocumentDescriptor? document = _context.GetActiveDocument();
        if (document is null)
        {
            return Failure(
                request.RequestId,
                BridgeErrorCodes.NoActiveDocument,
                "Revit has no active document.",
                started);
        }

        return Success(request.RequestId, document, document.Revision, started);
    }

    private static BridgeResponse Success<T>(
        string requestId,
        T result,
        string? documentRevision,
        long started)
    {
        ContractValidator.Validate(result);
        return new BridgeResponse
        {
            RequestId = requestId,
            Success = true,
            Result = JsonSerializer.SerializeToElement(result, ContractJson.Options),
            Warnings = [],
            DocumentRevision = documentRevision,
            DurationMs = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond
        };
    }

    private static BridgeResponse Failure(
        string requestId,
        string code,
        string message,
        long started) => new()
        {
            RequestId = requestId,
            Success = false,
            Warnings = [],
            Error = new BridgeError(code, message),
            DurationMs = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond
        };
}

using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Adapters;

public interface IRevitContextSnapshotProvider
{
    SessionDescriptor GetSessionSnapshot();

    DocumentDescriptor? GetActiveDocument();
}

public sealed class BridgeRequestProcessor
{
    private readonly IRevitContextSnapshotProvider _context;
    private readonly IReadOperationDispatcher? _reads;

    public BridgeRequestProcessor(IRevitContextSnapshotProvider context)
        : this(context, null)
    {
    }

    internal BridgeRequestProcessor(IRevitContextSnapshotProvider context, IReadOperationDispatcher? reads)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _reads = reads;
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
            _ when BridgeOperations.Reads.Contains(request.Operation) => ProcessRead(request, started),
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

    private BridgeResponse ProcessRead(BridgeRequest request, long started)
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

        DocumentDescriptor? document = session.Documents.SingleOrDefault(candidate =>
            string.Equals(candidate.DocumentKey, request.DocumentKey, StringComparison.Ordinal));
        if (document is null)
        {
            return Failure(
                request.RequestId,
                BridgeErrorCodes.DocumentNotFound,
                "The requested Revit document is not open in this session.",
                started);
        }

        if (_reads is null)
        {
            return Failure(
                request.RequestId,
                BridgeErrorCodes.OperationNotSupported,
                "The requested read operation is not available in the current bridge phase.",
                started);
        }

        try
        {
            ReadOperationResult result = _reads.Process(request);
            return SuccessElement(request.RequestId, result.Result, result.DocumentRevision, started);
        }
        catch (ReadCursorException error)
        {
            return Failure(
                request.RequestId,
                error.ErrorCode,
                "The read operation could not be completed for the requested document state.",
                started);
        }
    }

    private static BridgeResponse SuccessElement(
        string requestId,
        JsonElement result,
        string documentRevision,
        long started) => new()
        {
            RequestId = requestId,
            Success = true,
            Result = result.Clone(),
            Warnings = [],
            DocumentRevision = documentRevision,
            DurationMs = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond
        };

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

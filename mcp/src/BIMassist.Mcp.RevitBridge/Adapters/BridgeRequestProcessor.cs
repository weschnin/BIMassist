using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Changes;
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
    private readonly IPlanOperationDispatcher? _plans;
    private readonly IApplyOperationDispatcher? _apply;

    public BridgeRequestProcessor(IRevitContextSnapshotProvider context)
        : this(context, null, null)
    {
    }

    internal BridgeRequestProcessor(IRevitContextSnapshotProvider context, IReadOperationDispatcher? reads)
        : this(context, reads, null)
    {
    }

    internal BridgeRequestProcessor(
        IRevitContextSnapshotProvider context,
        IReadOperationDispatcher? reads,
        IPlanOperationDispatcher? plans)
        : this(context, reads, plans, null)
    {
    }

    internal BridgeRequestProcessor(
        IRevitContextSnapshotProvider context,
        IReadOperationDispatcher? reads,
        IPlanOperationDispatcher? plans,
        IApplyOperationDispatcher? apply)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _reads = reads;
        _plans = plans;
        _apply = apply;
    }

    public BridgeResponse Process(BridgeRequest request, CancellationToken cancellationToken = default)
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
            BridgeOperations.PlanSetParameterValues => ProcessPlan(request, started),
            BridgeOperations.ApplyChangePlan => ProcessApply(request, started, cancellationToken),
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

    private BridgeResponse ProcessPlan(BridgeRequest request, long started)
    {
        SessionDescriptor session = _context.GetSessionSnapshot();
        if (!string.Equals(request.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            return Failure(request.RequestId, BridgeErrorCodes.SessionNotFound,
                "The requested Revit session is not active.", started);
        }
        DocumentDescriptor? document = session.Documents.SingleOrDefault(candidate =>
            string.Equals(candidate.DocumentKey, request.DocumentKey, StringComparison.Ordinal));
        if (document is null)
        {
            return Failure(request.RequestId, BridgeErrorCodes.DocumentNotFound,
                "The requested Revit document is not open in this session.", started);
        }
        if (document.IsFamilyDocument || document.IsReadOnly)
        {
            return Failure(request.RequestId, BridgeErrorCodes.DocumentNotWritable,
                "The requested document cannot be changed by this operation.", started);
        }
        if (!string.Equals(request.ExpectedRevision, document.Revision, StringComparison.Ordinal))
        {
            return Failure(request.RequestId, BridgeErrorCodes.DocumentChanged,
                "The requested document has changed.", started);
        }
        if (_plans is null)
        {
            return Failure(request.RequestId, BridgeErrorCodes.OperationNotSupported,
                "The requested plan operation is not available in the current bridge phase.", started);
        }
        try
        {
            ChangePlan plan = _plans.Plan(request);
            if (!string.Equals(plan.SessionId, session.SessionId, StringComparison.Ordinal) ||
                !string.Equals(plan.DocumentKey, document.DocumentKey, StringComparison.Ordinal) ||
                !string.Equals(plan.ExpectedRevision, document.Revision, StringComparison.Ordinal))
            {
                return Failure(request.RequestId, BridgeErrorCodes.DocumentChanged,
                    "The plan no longer matches the requested document state.", started);
            }
            return Success(request.RequestId, plan, plan.ExpectedRevision, started);
        }
        catch (ChangePlanFailure error)
        {
            return Failure(request.RequestId, error.Code,
                "The plan could not be created for the requested document state.", started);
        }
        catch (ReadCursorException error)
        {
            return Failure(request.RequestId, error.ErrorCode,
                "The plan could not be created for the requested document state.", started);
        }
    }

    private BridgeResponse ProcessApply(BridgeRequest request, long started, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Failure(request.RequestId, BridgeErrorCodes.InvalidRequest,
                "The apply request was abandoned before execution.", started);
        SessionDescriptor session = _context.GetSessionSnapshot();
        if (!string.Equals(request.SessionId, session.SessionId, StringComparison.Ordinal))
            return Failure(request.RequestId, BridgeErrorCodes.SessionNotFound,
                "The requested Revit session is not active.", started);
        DocumentDescriptor? document = session.Documents.SingleOrDefault(candidate =>
            string.Equals(candidate.DocumentKey, request.DocumentKey, StringComparison.Ordinal));
        if (document is null)
            return Failure(request.RequestId, BridgeErrorCodes.DocumentNotFound,
                "The requested Revit document is not open in this session.", started);
        if (document.IsFamilyDocument || document.IsReadOnly)
            return Failure(request.RequestId, BridgeErrorCodes.DocumentNotWritable,
                "The requested document is not writable.", started);
        if (_apply is null)
            return Failure(request.RequestId, BridgeErrorCodes.OperationNotSupported,
                "Apply is not available in the current bridge.", started);
        try
        {
            ApplyOutcome outcome = _apply.Apply(request, cancellationToken);
            if (outcome.State != WriteOutcome.Committed)
            {
                string state = outcome.State == WriteOutcome.RolledBack ? "rolledBack" : "outcomeUnknown";
                return new BridgeResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Warnings = [],
                    Error = new BridgeError(BridgeErrorCodes.TransactionFailed,
                        "The plan was not confirmed as committed; do not retry with a new key.",
                        new Dictionary<string, string> { ["state"] = state }),
                    DurationMs = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond
                };
            }
            JsonElement result = JsonSerializer.SerializeToElement(new
            {
                state = outcome.State switch
                {
                    WriteOutcome.Committed => "committed",
                    WriteOutcome.RolledBack => "rolledBack",
                    _ => "outcomeUnknown"
                },
                documentRevision = outcome.DocumentRevision
            });
            return SuccessElement(request.RequestId, result,
                outcome.DocumentRevision ?? document.Revision, started);
        }
        catch (ChangePlanFailure error)
        {
            return Failure(request.RequestId, error.Code,
                "The write was not started for the requested plan.", started);
        }
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

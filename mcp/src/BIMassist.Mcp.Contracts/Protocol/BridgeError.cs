namespace BIMassist.Mcp.Contracts.Protocol;

public sealed record BridgeError(
    [property: System.Text.Json.Serialization.JsonRequired] string Code,
    [property: System.Text.Json.Serialization.JsonRequired] string Message,
    IReadOnlyDictionary<string, string>? Details = null);

public static class BridgeErrorCodes
{
    public const string RevitNotRunning = "REVIT_NOT_RUNNING";
    public const string NoCompatibleSession = "NO_COMPATIBLE_SESSION";
    public const string NoActiveDocument = "NO_ACTIVE_DOCUMENT";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
    public const string DocumentChanged = "DOCUMENT_CHANGED";
    public const string DocumentNotWritable = "DOCUMENT_NOT_WRITABLE";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string ParameterReadOnly = "PARAMETER_READ_ONLY";
    public const string FormulaControlled = "FORMULA_CONTROLLED";
    public const string TypeMismatch = "TYPE_MISMATCH";
    public const string UnitError = "UNIT_ERROR";
    public const string WorksharingOwnership = "WORKSHARING_OWNERSHIP";
    public const string PlanNotFound = "PLAN_NOT_FOUND";
    public const string PlanExpired = "PLAN_EXPIRED";
    public const string PlanHashMismatch = "PLAN_HASH_MISMATCH";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidCursor = "INVALID_CURSOR";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string SnapshotNotFound = "SNAPSHOT_NOT_FOUND";
    public const string SnapshotExpired = "SNAPSHOT_EXPIRED";
    public const string RequestTimeout = "REQUEST_TIMEOUT";
    public const string TransactionFailed = "TRANSACTION_FAILED";
    public const string ProtocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH";
    public const string OperationNotSupported = "OPERATION_NOT_SUPPORTED";
    public const string BridgeInternalError = "BRIDGE_INTERNAL_ERROR";
}

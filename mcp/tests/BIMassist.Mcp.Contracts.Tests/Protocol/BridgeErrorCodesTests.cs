using System.Reflection;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Tests.Protocol;

public sealed class BridgeErrorCodesTests
{
    [Fact]
    public void Error_codes_are_unique_and_match_the_version_one_contract()
    {
        string[] actual = typeof(BridgeErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "AMBIGUOUS_TARGET",
            "BRIDGE_INTERNAL_ERROR",
            "DOCUMENT_CHANGED",
            "DOCUMENT_NOT_FOUND",
            "DOCUMENT_NOT_WRITABLE",
            "FORMULA_CONTROLLED",
            "IDEMPOTENCY_CONFLICT",
            "INVALID_CURSOR",
            "INVALID_REQUEST",
            "LIMIT_EXCEEDED",
            "NO_ACTIVE_DOCUMENT",
            "NO_COMPATIBLE_SESSION",
            "OPERATION_NOT_SUPPORTED",
            "PARAMETER_READ_ONLY",
            "PLAN_EXPIRED",
            "PLAN_HASH_MISMATCH",
            "PLAN_NOT_FOUND",
            "PROTOCOL_VERSION_MISMATCH",
            "REQUEST_TIMEOUT",
            "REVIT_NOT_RUNNING",
            "SESSION_NOT_FOUND",
            "SNAPSHOT_EXPIRED",
            "SNAPSHOT_NOT_FOUND",
            "TARGET_NOT_FOUND",
            "TRANSACTION_FAILED",
            "TYPE_MISMATCH",
            "UNIT_ERROR",
            "WORKSHARING_OWNERSHIP"
        ];

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }
}

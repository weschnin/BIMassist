using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Changes;

public sealed record ChangePlan
{
    [JsonRequired]
    public required string PlanId { get; init; }

    [JsonRequired]
    public required string SessionId { get; init; }

    [JsonRequired]
    public required string DocumentKey { get; init; }

    [JsonRequired]
    public required string ExpectedRevision { get; init; }

    [JsonRequired]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonRequired]
    public required IReadOnlyList<ChangeOperation> Operations { get; init; }

    [JsonRequired]
    public required IReadOnlyList<BridgeWarning> Warnings { get; init; }

    [JsonRequired]
    public required string PlanHash { get; init; }
}

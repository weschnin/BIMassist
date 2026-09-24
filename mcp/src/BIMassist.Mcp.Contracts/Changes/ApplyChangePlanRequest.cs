using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Changes;

public sealed record ApplyChangePlanRequest
{
    [JsonRequired]
    public required string PlanId { get; init; }

    [JsonRequired]
    public required string PlanHash { get; init; }

    [JsonRequired]
    public required string ExpectedRevision { get; init; }

    [JsonRequired]
    public required string IdempotencyKey { get; init; }

    [JsonRequired]
    public required ApprovalMetadata Approval { get; init; }
}

public sealed record ApprovalMetadata
{
    [JsonRequired]
    public required string ApprovedBy { get; init; }

    [JsonRequired]
    public required DateTimeOffset ApprovedAtUtc { get; init; }

    [JsonRequired]
    public required string Source { get; init; }
}

using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Documents;

public enum DocumentPathStatus
{
    Unsaved,
    Saved,
    Cloud,
    Detached
}

public sealed record DocumentDescriptor
{
    [JsonRequired]
    public required string DocumentKey { get; init; }

    [JsonRequired]
    public required string Title { get; init; }

    [JsonRequired]
    public required DocumentPathStatus PathStatus { get; init; }

    public string? Path { get; init; }

    [JsonRequired]
    public required bool IsFamilyDocument { get; init; }

    [JsonRequired]
    public required bool IsWorkshared { get; init; }

    [JsonRequired]
    public required bool IsReadOnly { get; init; }

    public string? ActiveViewUniqueId { get; init; }

    [JsonRequired]
    public required string Revision { get; init; }
}

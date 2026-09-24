using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Adapters;

internal sealed class RevitReadOperationDispatcher : IReadOperationDispatcher
{
    private readonly IRevitFamilyReadSource _families;
    private readonly FamilyReadService _familyService;

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService)
    {
        _families = families ?? throw new ArgumentNullException(nameof(families));
        _familyService = familyService ?? throw new ArgumentNullException(nameof(familyService));
    }

    public ReadOperationResult Process(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        return request.Operation switch
        {
            BridgeOperations.ListFamilies => ListFamilies(request),
            BridgeOperations.GetFamilyMetadata => GetFamilyMetadata(request),
            _ => throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported)
        };
    }

    private ReadOperationResult ListFamilies(BridgeRequest request)
    {
        ListFamiliesRequest payload = ContractJson.Deserialize<ListFamiliesRequest>(request.Payload.GetRawText());
        FamilyReadSnapshot snapshot = ReadSnapshot(request);
        PageResult<FamilySummary> result = _familyService.ListFamilies(
            snapshot.Families,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult GetFamilyMetadata(BridgeRequest request)
    {
        GetFamilyMetadataRequest payload = ContractJson.Deserialize<GetFamilyMetadataRequest>(request.Payload.GetRawText());
        FamilyReadSnapshot snapshot = ReadSnapshot(request);
        FamilyMetadata result = _familyService.GetFamilyMetadata(
            snapshot.Families,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private FamilyReadSnapshot ReadSnapshot(BridgeRequest request) =>
        _families.ReadFamilies(request.SessionId!, request.DocumentKey!);

    private static ReadOperationResult Serialize<T>(T result, string revision)
    {
        ContractValidator.Validate(result);
        JsonElement json = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        return new ReadOperationResult(json, revision);
    }
}

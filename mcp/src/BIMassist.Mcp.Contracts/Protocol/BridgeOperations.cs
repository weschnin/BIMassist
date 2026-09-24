namespace BIMassist.Mcp.Contracts.Protocol;

public static class BridgeOperations
{
    public const string GetStatus = "status.get";
    public const string GetDocumentContext = "document.context.get";
    public const string ListFamilies = "family.list";
    public const string GetFamilyMetadata = "family.metadata.get";
    public const string ListParameters = "parameter.list";
    public const string GetParameterMetadata = "parameter.metadata.get";
    public const string ListSharedDefinitions = "sharedDefinition.list";
    public const string ListProjectBindings = "projectBinding.list";
    public const string GetParameterValues = "parameterValue.get";
    public const string ExportMetadataSnapshot = "metadataSnapshot.export";
    public const string PlanSetParameterValues = "parameterValue.set.plan";
    public const string PlanAddSharedParameterToFamily = "sharedParameter.family.add.plan";
    public const string PlanBindSharedParameter = "sharedParameter.bind.plan";
    public const string ApplyChangePlan = "changePlan.apply";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        GetStatus,
        GetDocumentContext,
        ListFamilies,
        GetFamilyMetadata,
        ListParameters,
        GetParameterMetadata,
        ListSharedDefinitions,
        ListProjectBindings,
        GetParameterValues,
        ExportMetadataSnapshot,
        PlanSetParameterValues,
        PlanAddSharedParameterToFamily,
        PlanBindSharedParameter,
        ApplyChangePlan
    };

    public static IReadOnlySet<string> RequiresSession { get; } =
        new HashSet<string>(All.Where(operation => operation != GetStatus), StringComparer.Ordinal);

    public static IReadOnlySet<string> RequiresDocument { get; } =
        new HashSet<string>(All.Where(operation => operation is not GetStatus and not GetDocumentContext), StringComparer.Ordinal);

    public static IReadOnlySet<string> Changes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        PlanSetParameterValues,
        PlanAddSharedParameterToFamily,
        PlanBindSharedParameter,
        ApplyChangePlan
    };
}

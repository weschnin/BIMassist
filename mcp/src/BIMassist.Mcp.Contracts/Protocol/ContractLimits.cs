namespace BIMassist.Mcp.Contracts.Protocol;

public static class ContractLimits
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 1_000;
    public const int MaximumContractBytes = 1_048_576;
    public const int MaximumPayloadBytes = 1_048_576;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumDocumentKeyLength = 512;
    public const int MaximumRevisionLength = 256;
    public const int MaximumCursorLength = 1_024;
    public const int MaximumTextLength = 4_096;
    public const int MaximumValueTextLength = 32_768;
    public const int MaximumOperationsPerPlan = 1_000;
    public const int MaximumWarnings = 1_000;
    public const int MaximumSessionDocuments = 1_000;
    public const int MaximumBindingCategories = 1_000;
    public const int MaximumParameterSelectors = 1_000;
    public const int MaximumOperationOptions = 100;
    public const int MaximumErrorDetails = 100;
}

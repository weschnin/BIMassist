using System.Globalization;
using System.Text;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Non-wired, bounded, complete approval text for 2–20 string writes.</summary>
internal static class BatchApplyApprovalPrompt
{
    internal const int MaximumPromptLength = 16_384;

    internal static string FixedHashText(string planHash) => "Plan-Hash (SHA-256): " + planHash;

    internal static string Format(ChangePlan plan, string documentTitle)
    {
        if (plan is null || plan.Operations is null || plan.Operations.Count is < 2 or > 20 ||
            string.IsNullOrWhiteSpace(plan.SessionId) || string.IsNullOrWhiteSpace(plan.DocumentKey) ||
            string.IsNullOrWhiteSpace(plan.PlanHash) || documentTitle is null ||
            plan.Warnings is null || plan.Warnings.Count != 0)
            throw Invalid();
        BatchStringWritePreconditions.Validate(plan);

        var text = new StringBuilder();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        Append($"Genau diese {plan.Operations.Count} Parameteränderungen erlauben?\n\n", text);
        Append($"Plan-ID: {Safe(plan.PlanId)}\nErwartete Revision: {Safe(plan.ExpectedRevision)}\n" +
               $"Erstellt (UTC): {plan.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}\n" +
               $"Gültig bis (UTC): {plan.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}\n" +
               $"Sitzung: {Safe(plan.SessionId)}\nDokument: {Safe(documentTitle)}\n" +
               $"Dokumentschlüssel: {Safe(plan.DocumentKey)}\n", text);
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            ChangeOperation operation = plan.Operations[index];
            if (operation is null || string.IsNullOrWhiteSpace(operation.OperationId) ||
                !operationIds.Add(operation.OperationId) ||
                operation.Kind != ChangeOperationKind.SetParameterValue ||
                operation.Options is null || operation.Options.Count != 0 ||
                operation.Target is null || operation.Target.FamilyName is not null ||
                operation.Target.TypeName is not null || operation.Parameter is null ||
                operation.Before is null || operation.After is null ||
                operation.Before.Kind is not (ParameterValueKind.String or ParameterValueKind.None) ||
                operation.After.Kind != ParameterValueKind.String || !operation.After.HasValue ||
                operation.After.StringValue is null ||
                operation.After.DisplayValue is not null || operation.After.SpecTypeId is not null ||
                operation.After.InputUnitTypeId is not null || operation.After.Formula is not null ||
                operation.After.IntegerValue is not null || operation.After.DoubleValue is not null ||
                operation.After.InternalDoubleValue is not null || operation.After.BooleanValue is not null ||
                operation.After.Reference is not null ||
                operation.Before.SpecTypeId is not null || operation.Before.InputUnitTypeId is not null ||
                operation.Before.Formula is not null || operation.Before.IntegerValue is not null ||
                operation.Before.DoubleValue is not null || operation.Before.InternalDoubleValue is not null ||
                operation.Before.BooleanValue is not null || operation.Before.Reference is not null ||
                (operation.Before.HasValue && operation.Before.StringValue is null) ||
                operation.BeforeBinding is not null || operation.AfterBinding is not null ||
                operation.BeforeDefinition is not null || operation.AfterDefinition is not null)
                throw Invalid();
            string target = operation.Target.Kind switch
            {
                ParameterTargetKind.Document when operation.Target.UniqueId is null &&
                    operation.Target.ElementId is null => "Projektinformationen (Dokument)",
                ParameterTargetKind.Element or ParameterTargetKind.ElementType
                    when !string.IsNullOrWhiteSpace(operation.Target.UniqueId) && operation.Target.ElementId is >= 0 =>
                    $"{Safe(operation.Target.UniqueId)} ({operation.Target.ElementId.Value.ToString(CultureInfo.InvariantCulture)}) [{operation.Target.Kind}]",
                _ => throw Invalid()
            };
            string identity = operation.Parameter.Kind switch
            {
                ParameterIdentityKind.SharedGuid when operation.Parameter.SharedGuid is not null =>
                    $"GUID {operation.Parameter.SharedGuid.Value:D}",
                ParameterIdentityKind.BuiltIn when operation.Parameter.BuiltInId is not null =>
                    $"Built-in ID {operation.Parameter.BuiltInId.Value.ToString(CultureInfo.InvariantCulture)}",
                ParameterIdentityKind.ParameterElement when operation.Parameter.DefinitionId is > 0 =>
                    $"ParameterElement-ID {operation.Parameter.DefinitionId.Value.ToString(CultureInfo.InvariantCulture)}",
                _ => throw Invalid()
            };
            string before = operation.Before.HasValue ? $"\"{Safe(operation.Before.StringValue!)}\"" : "<kein Wert>";
            string beforeDisplay = operation.Before.DisplayValue is null ? "" :
                $"Vorher (Anzeigewert): \"{Safe(operation.Before.DisplayValue)}\"\n";
            string context = operation.Parameter.OwnerContext is null ? "" :
                $" [Eigentümer: {Safe(operation.Parameter.OwnerContext)}]";
            string dataType = operation.Parameter.DataTypeId is null ? "" :
                $" [Datentyp: {Safe(operation.Parameter.DataTypeId)}]";
            string instance = operation.Parameter.IsInstance is null ? "" :
                $" [Instanz: {operation.Parameter.IsInstance.Value}]";
            Append($"\nÄnderung {index + 1} ({Safe(operation.OperationId)}):\n" +
                   $"Element: {target}\n" +
                   $"Parameter: {identity} [Stable-ID: {Safe(operation.Parameter.StableId)}] ({Safe(operation.Parameter.Name)}){context}{dataType}{instance}\n" +
                   $"Vorher: {before}\n{beforeDisplay}Nachher: \"{Safe(operation.After.StringValue)}\"\n", text);
        }
        try
        {
            ContractValidator.Validate(plan);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        if (plan.PlanHash.Length != 64 || !plan.PlanHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !string.Equals(plan.PlanHash, ChangePlanHasher.ComputeHash(plan), StringComparison.Ordinal))
            throw Invalid();
        string fixedHash = FixedHashText(plan.PlanHash);
        Append("\n" + fixedHash, text);
        // The WPF dialog displays the hash a second time, in its fixed visible region.
        if (fixedHash.Length > MaximumPromptLength - text.Length)
            throw Invalid();
        return text.ToString();
    }

    private static void Append(string part, StringBuilder text)
    {
        if (part.Length > MaximumPromptLength - text.Length)
            throw Invalid();
        text.Append(part);
    }

    private static string Safe(string? value)
    {
        if (value is null || value.Length > MaximumPromptLength)
            throw Invalid();
        var text = new StringBuilder();
        foreach (char character in value)
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || char.IsSurrogate(character) ||
                category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else if (character is '\\' or '"')
                text.Append('\\').Append(character);
            else
                text.Append(character);
            if (text.Length > MaximumPromptLength)
                throw Invalid();
        }
        return text.ToString();
    }

    private static ChangePlanFailure Invalid() => new(BridgeErrorCodes.InvalidRequest);
}

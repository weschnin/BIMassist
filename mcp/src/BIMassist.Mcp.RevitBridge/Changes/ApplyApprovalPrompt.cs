using System.Globalization;
using System.Text;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Bounded, unambiguous human-readable view of the exact hashed write plan.</summary>
internal static class ApplyApprovalPrompt
{
    internal static string Format(ChangePlan plan, string documentTitle)
    {
        ChangeOperation operation = StringWritePreconditions.Single(plan.Operations);
        string identity = operation.Parameter.Kind switch
        {
            ParameterIdentityKind.SharedGuid => $"GUID {operation.Parameter.SharedGuid}",
            ParameterIdentityKind.BuiltIn => $"Built-in ID {operation.Parameter.BuiltInId}",
            ParameterIdentityKind.ParameterElement =>
                $"ParameterElement-ID {operation.Parameter.DefinitionId} ({Safe(operation.Parameter.StableId)})",
            _ => throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest)
        };
        string before = operation.Before!.HasValue ? operation.Before.StringValue ?? "" : "<kein Wert>";
        string prompt = $"Sitzung: {Safe(plan.SessionId)}\n" +
                        $"Dokument: {Safe(documentTitle)}\n" +
                        $"Dokumentschlüssel: {Safe(plan.DocumentKey)}\n" +
                        $"Element: {Safe(operation.Target.UniqueId ?? "Projektinformationen")} ({operation.Target.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "–"})\n" +
                        $"Parameter: {identity} ({Safe(operation.Parameter.Name)})\n" +
                        $"Vorher: {Safe(before)}\nNachher: {Safe(operation.After!.StringValue!)}\n" +
                        $"Plan-Hash (SHA-256): {Safe(plan.PlanHash)}";
        if (prompt.Length > 4096)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        return prompt;
    }

    private static string Safe(string value)
    {
        if (value.Length > 512)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        var text = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || char.IsSurrogate(character) ||
                category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                text.Append(character);
        }
        if (text.Length > 1024)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        return text.ToString();
    }
}

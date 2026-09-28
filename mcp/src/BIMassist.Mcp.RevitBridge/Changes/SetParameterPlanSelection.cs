using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Selects one materialized parameter using only its Revit-derived write identity.</summary>
internal static class SetParameterPlanSelection
{
    internal static ResolvedSetParameterPlan Select(ParameterReadSnapshot snapshot, ParameterIdentity requested)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Kind is not (ParameterIdentityKind.SharedGuid or ParameterIdentityKind.BuiltIn))
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);

        ParameterReadRecord? selected = null;
        foreach (ParameterReadRecord record in snapshot.Parameters)
        {
            ParameterIdentity actual = record.Summary.Identity;
            bool matches = requested.Kind == actual.Kind && (requested.Kind switch
            {
                ParameterIdentityKind.SharedGuid => requested.SharedGuid is not null && actual.SharedGuid == requested.SharedGuid,
                ParameterIdentityKind.BuiltIn => requested.BuiltInId is not null && actual.BuiltInId == requested.BuiltInId,
                _ => false
            });
            if (!matches) continue;
            if (selected is not null) throw new ChangePlanFailure(BridgeErrorCodes.AmbiguousTarget);
            selected = record;
        }
        if (selected is null || selected.Value is null)
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        if (selected.Metadata.StorageType != ParameterStorageType.String ||
            selected.Summary.StorageType != ParameterStorageType.String ||
            selected.Value.Kind is not (ParameterValueKind.String or ParameterValueKind.None))
            throw new ChangePlanFailure(BridgeErrorCodes.TypeMismatch);
        if (selected.Metadata.BlockedReason == "element_owned_by_other_user" ||
            !selected.Metadata.Worksharing.IsEditable)
            throw new ChangePlanFailure(BridgeErrorCodes.WorksharingOwnership);
        if (selected.Metadata.IsReadOnly || selected.Summary.IsReadOnly ||
            selected.Value.IsReadOnly || !selected.Metadata.Definition.IsUserModifiable ||
            selected.Metadata.BlockedReason is not null)
            throw new ChangePlanFailure(BridgeErrorCodes.ParameterReadOnly);
        return new ResolvedSetParameterPlan(snapshot.Target, selected.Summary.Identity,
            selected.Value, snapshot.DocumentRevision);
    }
}

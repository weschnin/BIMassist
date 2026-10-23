using System.IO;

namespace BIMassist.Core
{
    internal sealed class Bgk3DTextBatch
    {
        private Bgk3DTextBatch(IReadOnlyList<Bgk3DTextSpec> types)
        {
            Types = types;
        }

        internal IReadOnlyList<Bgk3DTextSpec> Types { get; }
        internal string FamilyName => Types[0].FamilyName;
        internal string PlacementTypeName => Types[0].TypeName;

        internal static bool TryCreate(IEnumerable<Bgk3DTextSpec> selection,
            out Bgk3DTextBatch? batch, out string error)
        {
            batch = null;
            error = string.Empty;
            Bgk3DTextSpec[] types = selection?.ToArray() ?? Array.Empty<Bgk3DTextSpec>();
            if (types.Length == 0 || types.Any(t => t == null))
            {
                error = "Bitte mindestens einen gültigen BGK-Typ auswählen.";
                return false;
            }
            if (types.Any(t => !string.Equals(t.FamilyName, types[0].FamilyName, StringComparison.Ordinal)))
            {
                error = "Es können nur Typen derselben BGK-Familie gemeinsam erzeugt werden.";
                return false;
            }
            if (types.Select(t => t.TypeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Length)
            {
                error = "Die ausgewählten Typen enthalten doppelte Typbezeichnungen.";
                return false;
            }
            batch = new Bgk3DTextBatch(Array.AsReadOnly(types));
            return true;
        }

        internal IReadOnlyList<string> MissingFrom(IEnumerable<string> existingNames)
        {
            var known = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
            return Types.Select(t => t.TypeName).Where(name => !known.Contains(name)).ToArray();
        }

        internal static bool ShouldRebuildVisibility(int newlyAddedTypes) => newlyAddedTypes > 0;

        internal static bool ShouldInitializeAvaDefault(bool isNewType, bool hasValue) =>
            isNewType || !hasValue;

        internal static bool IsSafeFamilyReload(string expectedName, string loadedName,
            long loadedId, long? priorFamilyId, IReadOnlyCollection<long> priorFamilyIds,
            IReadOnlyCollection<long> matchingFamilyIds, bool priorFamilyStillPresent)
        {
            if (!string.Equals(expectedName, loadedName, StringComparison.OrdinalIgnoreCase)
                || matchingFamilyIds.Count != 1 || !matchingFamilyIds.Contains(loadedId))
                return false;
            if (priorFamilyId == null)
                return !priorFamilyIds.Contains(loadedId);
            if (loadedId == priorFamilyId.Value)
                return true;
            // Revit darf beim Reload neu nummerieren, aber nur, wenn die alte
            // Familie verschwunden und keine andere bestehende Familie ersetzt ist.
            return !priorFamilyStillPresent && !priorFamilyIds.Contains(loadedId);
        }

        internal bool IsVisibleFor(string ownerTypeName, string activeTypeName) =>
            string.Equals(ownerTypeName, activeTypeName, StringComparison.OrdinalIgnoreCase);

        internal static string VisibilityParameterName(string typeName)
        {
            // Revit verbietet u. a. ':' und '\\' in Parameternamen; die Typbezeichnung
            // darf diese Zeichen enthalten. Der Hash trennt Namen, die durch die
            // lesbare Bereinigung oder Kürzung sonst zusammenfallen würden.
            string readable = new string(typeName.Select(c =>
                char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '-').ToArray())
                .Trim(' ', '-', '.');
            if (readable.Length > 80) readable = readable[..80].TrimEnd(' ', '-', '.');
            if (readable.Length == 0) readable = "Typ";
            byte[] digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(typeName.ToUpperInvariant()));
            return $"BGK sichtbar - {readable} - {Convert.ToHexString(digest)[..24]}";
        }

        internal static string ChildFamilyName(string familyName, string typeName)
        {
            string identity = (familyName + "\n" + typeName).ToUpperInvariant();
            string readable = new string(typeName.Select(c =>
                char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '-').ToArray())
                .Trim(' ', '-', '.');
            if (readable.Length > 75) readable = readable[..75].TrimEnd(' ', '-', '.');
            if (readable.Length == 0) readable = "Typ";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity));
            return $"BGK Text - {readable} - {Convert.ToHexString(hash)[..24]}";
        }

        internal static string? FindChildOwner(string outerName, IReadOnlyList<string> typeNames,
            string? storedOwner, string? childFamilyName)
        {
            // Ein EditFamily-Readback kann den Instanz-Marker verlieren. In diesem Fall
            // nur den eindeutigen, aus Außenfamilie und Typ abgeleiteten Kindnamen
            // verwenden; die exakte Parameterbindung wird separat geprüft.
            string? match = null;
            foreach (string typeName in typeNames)
            {
                if (!string.Equals(ChildFamilyName(outerName, typeName), childFamilyName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null || (!string.IsNullOrWhiteSpace(storedOwner)
                    && !string.Equals(storedOwner, typeName, StringComparison.OrdinalIgnoreCase)))
                    return null;
                match = typeName;
            }
            return match;
        }

        internal static string? ChildLoadFailure(string expectedName, string? actualName, bool loaded)
        {
            if (!loaded) return "Revit hat das Laden abgelehnt (LoadFamily=false).";
            if (actualName == null) return "Revit hat keine Kindfamilie zurückgegeben.";
            return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
                ? null : $"Revit hat stattdessen '{actualName}' geladen.";
        }

        internal static string BindingObservation(string stage, long instanceId, long visibleId,
            long flagId, bool associable, long? forwardId, bool reverseContains, int reverseCount) =>
            $"{stage}: Instanz {instanceId}, Sichtbar-ID {visibleId}, Flag-ID {flagId}, "
            + $"zuordenbar {(associable ? "ja" : "nein")}, "
            + $"vorwärts {(forwardId?.ToString() ?? "keine")}, "
            + $"rückwärts {(reverseContains ? "ja" : "nein")} ({reverseCount})";

        internal static string BindingProbeObservation(string stage, long instanceId, long visibleId,
            long flagId, bool associable, long? forwardId, long? reverseForwardId,
            bool reverseContains, int reverseCount, string? reverseError = null) =>
            BindingObservation(stage, instanceId, visibleId, flagId, associable,
                forwardId, reverseContains, reverseCount)
            + ", vorwärts über Rückwärtsobjekt "
            + (reverseError == null ? reverseForwardId?.ToString() ?? "keine" : $"Fehler {reverseError}");

        internal static string DiagnosticFamilyFileName(DateTime utc, Guid id) =>
            $"BGK-Familientest-{utc:yyyyMMdd-HHmmss}-{id:N}.rfa";

        internal static string? FindGenericModelTemplate(string? configuredPath, string appTemplatePath,
            string installedTemplatesRoot, Func<string, bool> exists)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return exists(configuredPath) ? configuredPath : null;

            string[] names = { "Allgemeine Familie.rft", "Metric Generic Model.rft", "Generic Model.rft" };
            IEnumerable<string> candidates = names.Select(name => Path.Combine(appTemplatePath, name))
                .Concat(new[]
                {
                    Path.Combine(installedTemplatesRoot, "German", "Allgemeine Familie.rft"),
                    Path.Combine(installedTemplatesRoot, "English", "Metric Generic Model.rft"),
                    Path.Combine(installedTemplatesRoot, "English-Imperial", "Generic Model.rft")
                });
            return candidates.FirstOrDefault(exists);
        }

        internal static bool HasExclusiveVisibility(IReadOnlyList<string> typeNames,
            Func<string, string, int?> readValue)
        {
            foreach (string active in typeNames)
                foreach (string owner in typeNames)
                {
                    int expected = string.Equals(active, owner, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    if (readValue(active, owner) != expected) return false;
                }
            return typeNames.Count > 0;
        }

        internal static IReadOnlyList<string> ObsoleteTemplateTypes(IEnumerable<string> templateNames,
            IEnumerable<string> newlyRequested)
        {
            HashSet<string> requested = new(newlyRequested, StringComparer.OrdinalIgnoreCase);
            return templateNames.Where(name => !requested.Contains(name)).ToArray();
        }

        internal static IReadOnlyList<long> NewInstanceIds(IEnumerable<long> before, IEnumerable<long> after)
        {
            HashSet<long> oldIds = new(before);
            return after.Where(id => !oldIds.Contains(id)).ToArray();
        }

        internal static bool InstanceValuesPreserved(IReadOnlyList<string> before, IReadOnlyList<string> after,
            string newParameterName, bool parameterAdded)
        {
            if (before.SequenceEqual(after)) return true;
            if (!parameterAdded || after.Count != before.Count + 1) return false;
            string[] added = after.Where(value =>
            {
                string[] parts = value.Split('|', 3);
                return parts.Length > 1 && parts[1] == newParameterName;
            }).ToArray();
            return added.Length == 1 && after.Except(added).SequenceEqual(before);
        }

        internal static bool HasCorrectAssociationOwners(IReadOnlyDictionary<long, string> expectedOwners,
            Func<long, string?> readOwner)
        {
            if (expectedOwners.Count == 0) return false;
            foreach ((long elementId, string expectedOwner) in expectedOwners)
                if (!string.Equals(readOwner(elementId), expectedOwner, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        internal static bool HasExactAssociations(IReadOnlyDictionary<long, long> expectedFlags,
            Func<long, IReadOnlyList<long>> reverseOwners, Func<long, long?> forwardOwner)
        {
            if (expectedFlags.Count == 0) return false;
            foreach ((long instanceId, long expectedFlagId) in expectedFlags)
            {
                IReadOnlyList<long>? matches = reverseOwners(instanceId);
                if (matches == null || matches.Count != 1 || matches[0] != expectedFlagId)
                    return false;
                long? forward = forwardOwner(instanceId);
                if (forward.HasValue && forward.Value != expectedFlagId) return false;
            }
            return true;
        }

        internal static bool IsLegacyType(string familyName, string typeName, string? assembly,
            string? comments, string? description) =>
            string.Equals(assembly, familyName, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(comments) && !string.IsNullOrWhiteSpace(description)
            && string.Equals(typeName, comments + " " + description, StringComparison.Ordinal);
    }

    internal static class BgkTypeSelectionRules
    {
        internal static bool CanTransfer(int count) => count == 1;
        internal static bool CanGenerate(int count) => count > 0;
    }
}

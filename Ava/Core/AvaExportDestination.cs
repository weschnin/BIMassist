using System.IO;
using System.Security.Cryptography;

namespace BIMassist.Ava.Core;

/// <summary>A checked export choice. Replacement requires an explicit host confirmation.</summary>
public sealed class AvaExportDestination
{
    private readonly string path;
    private readonly bool replaceExisting;
    private readonly string[] protectedPaths;
    private readonly FileState? approvedState;
    private int consumed;

    private AvaExportDestination(string path, bool replaceExisting, string[] protectedPaths, FileState? approvedState = null)
    {
        this.path = path;
        this.replaceExisting = replaceExisting;
        this.protectedPaths = protectedPaths;
        this.approvedState = approvedState;
    }

    /// <summary>Returns null on cancellation; a file picker confirmation is not overwrite consent.</summary>
    public static AvaExportDestination? Choose(string path, IEnumerable<string?> protectedPaths, Func<string, bool> confirmOverwrite,
        SourceStamp? source = null)
    {
        ArgumentNullException.ThrowIfNull(confirmOverwrite);
        var fullPath = Normalize(path);
        var protectedSnapshot = protectedPaths.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalize(value!)).ToArray();
        EnsureNotProtected(fullPath, protectedSnapshot);
        // Capture BEFORE asking: a confirmation must not authorize a file changed during the prompt.
        FileState? state;
        try { state = Capture(fullPath); }
        catch (FileNotFoundException) { state = null; }
        EnsureNotSource(fullPath, source, state);
        if (state != null && !confirmOverwrite(fullPath)) return null;
        return new AvaExportDestination(fullPath, state != null, protectedSnapshot, state);
    }

    public static AvaExportDestination NewFile(string path) => new(Normalize(path), false, []);

    public static void EnsureNotProtected(string path, IEnumerable<string?> protectedPaths)
    {
        var fullPath = Normalize(path);
        if (protectedPaths.Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => string.Equals(fullPath, Normalize(value!), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Die Exportdatei darf das Revit-Projekt, eine AVA-Vorlage, Arbeitsbereich-Einstellungen oder ein Quell-LV nicht überschreiben.");
    }

    /// <summary>Portable source stamps keep a basename, not the original directory. Protect bytes and ambiguous names.</summary>
    public static void EnsureNotSource(string path, SourceStamp source)
    {
        var fullPath = Normalize(path);
        FileState state;
        try { state = Capture(fullPath); }
        catch (FileNotFoundException) { return; }
        EnsureNotSource(fullPath, source, state);
    }

    private static void EnsureNotSource(string fullPath, SourceStamp? source, FileState? state)
    {
        if (source == null || state == null) return;
        if (string.Equals(state.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)
            || (!Path.IsPathFullyQualified(source.FileName)
                && string.Equals(Path.GetFileName(fullPath), Path.GetFileName(source.FileName), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Die vorhandene Datei ist ein Quell-LV oder kann wegen des portablen Quelldateinamens nicht sicher davon unterschieden werden. Wählen Sie einen neuen, anderen Dateinamen für den Export; das Quell-LV bleibt unverändert.");
        if (Path.IsPathFullyQualified(source.FileName)) EnsureNotProtected(fullPath, [source.FileName]);
    }

    /// <summary>Prepare and validate a sibling file before atomically publishing it. Never deletes the destination.</summary>
    public void Save(string path, Action<string> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        var fullPath = Normalize(path);
        if (!string.Equals(this.path, fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Überschreibbestätigung gilt nicht für diesen Dateipfad.");
        EnsureNotProtected(fullPath, protectedPaths);
        if (Interlocked.Exchange(ref consumed, 1) != 0)
            throw new IOException("Die Exportbestätigung wurde bereits verwendet. Wählen und bestätigen Sie die Datei erneut.");
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".ava-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            prepare(temporary);
            if (replaceExisting)
            {
                // Deny concurrent writes while checking/publishing; allow deletion so Windows can atomically replace.
                using var guard = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (Capture(fullPath, guard) != approvedState || Capture(fullPath) != approvedState)
                    throw new IOException("Die Zieldatei wurde seit der Überschreibbestätigung geändert. Export abgebrochen; wählen und bestätigen Sie die Datei erneut.");
                File.Replace(temporary, fullPath, null);
            }
            else File.Move(temporary, fullPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = fullPath[(Path.GetPathRoot(fullPath)?.Length ?? 0)..];
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal) || fullPath.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || relative.Contains(':') || relative.Split(['\\', '/']).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Der Exportpfad enthält einen mehrdeutigen Windows-Dateinamen oder Datenstrom. Wählen Sie einen normalen Dateipfad.");
        return fullPath;
    }

    private sealed record FileState(long Length, DateTime CreationUtc, DateTime WriteUtc, string Sha256);

    private static FileState Capture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Capture(path, stream);
    }

    private static FileState Capture(string path, FileStream stream)
    {
        var info = new FileInfo(path);
        return new FileState(stream.Length, info.CreationTimeUtc, info.LastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(stream)));
    }
}

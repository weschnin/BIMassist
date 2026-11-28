using System.Windows;
using Microsoft.Win32;
using BIMassist.Ava.Core;
using System.IO;

namespace BIMassist.Ava.Views;

/// <summary>Owns file-only children of Mengenermittlung, independent of Revit external events.</summary>
internal sealed class AvaFileViewerSession : IDisposable
{
    private readonly Window owner;
    private readonly Action<string> showStatus;
    private readonly Func<Window, string, string?> pickFile;
    private readonly Func<string>? templateSnapshot;
    private readonly Dictionary<string, Window> children = new(StringComparer.Ordinal);
    private bool closed;

    internal AvaFileViewerSession(Window owner, Action<string> showStatus,
        Func<Window, string, string?>? pickFile = null, Func<string>? templateSnapshot = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.showStatus = showStatus ?? throw new ArgumentNullException(nameof(showStatus));
        this.pickFile = pickFile ?? PickFile;
        this.templateSnapshot = templateSnapshot;
        owner.Closed += OnOwnerClosed;
        owner.Dispatcher.ShutdownStarted += OnOwnerClosed;
    }

    internal int ChildCount => children.Count;
    internal void OpenX31() => Open("x31");
    internal void OpenX82() => Open("x82");

    private void Open(string extension)
    {
        if (closed || owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished) return;
        Window? candidate = null;
        try
        {
            var path = pickFile(owner, extension);
            if (string.IsNullOrWhiteSpace(path) || closed) return;
            IReadOnlyDictionary<string, X31ReviewPosition>? positions = null;
            var metadataWarning = "";
            if (extension == "x31" && templateSnapshot != null)
            {
                try
                {
                    positions = TemplateJson.Deserialize(templateSnapshot()).LvCatalog.Entries.ToDictionary(item => item.Id,
                        item => new X31ReviewPosition(item.Ordinal, item.Text, item.Unit, item.BoqId), StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    metadataWarning = " LV-Ergänzung aus dem Lauf-Snapshot nicht verfügbar: " + ex.Message;
                }
            }
            candidate = extension == "x31" ? X31FileViewerWindow.Create(path, positions) : LvX82ViewerWindow.Create(path);
            candidate.Owner = owner;
            candidate.Closed += OnChildClosed;
            // Parse and show the replacement before retiring the previous, usable viewer.
            candidate.Show();
            if (children.TryGetValue(extension, out var previous)) previous.Close();
            children[extension] = candidate;
            showStatus(extension.ToUpperInvariant() + " Viewer geöffnet (schreibgeschützt)." + metadataWarning);
        }
        catch (Exception ex)
        {
            if (candidate != null) { candidate.Closed -= OnChildClosed; candidate.Close(); }
            if (!closed) showStatus(extension.ToUpperInvariant() + " Viewer konnte nicht geöffnet werden: " + ex.Message);
        }
    }

    private void OnChildClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Closed -= OnChildClosed;
        foreach (var entry in children.Where(entry => ReferenceEquals(entry.Value, window)).ToArray())
            children.Remove(entry.Key);
    }

    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        owner.Closed -= OnOwnerClosed;
        owner.Dispatcher.ShutdownStarted -= OnOwnerClosed;
        foreach (var child in children.Values.ToArray()) child.Close();
        children.Clear();
    }

    private static string? PickFile(Window owner, string extension)
    {
        var dialog = new OpenFileDialog
        {
            Title = extension.ToUpperInvariant() + " Viewer – Datei öffnen",
            Filter = $"GAEB-Datei (*.{extension})|*.{extension}|Alle Dateien (*.*)|*.*",
            FilterIndex = 1, DefaultExt = "." + extension, AddExtension = true,
            CheckFileExists = true, CheckPathExists = true, ValidateNames = true, Multiselect = false
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
using System.IO;
using Microsoft.Win32;

namespace BIMassist.Ava.Views;

/// <summary>Consistent, explicitly selectable file types for every file-only AVA editor operation.</summary>
public static class AvaFileDialogs
{
    private static string Filter(string label, string extension) => $"{label} (*{extension})|*{extension}|Alle Dateien (*.*)|*.*";

    public static OpenFileDialog OpenTemplate(bool import = false) => Open(
        import ? "Portable Vorlage importieren" : "AVA-Vorlage öffnen", "AVA-Vorlage", ".json");

    public static OpenFileDialog OpenCatalog(string kind) => kind switch
    {
        "bgk" => Open("BGK-XML importieren", "BGK-Katalog", ".xml"),
        "lv" => Open("LV X82 importieren", "GAEB DA82/3.3", ".x82"),
        "parameters" => Open("Gemeinsame Parameter importieren", "Gemeinsame Parameter", ".txt"),
        _ => throw new ArgumentException("Unbekannter AVA-Katalogtyp.", nameof(kind))
    };

    private static OpenFileDialog Open(string title, string label, string extension) => new()
    {
        Title = title, Filter = Filter(label, extension), FilterIndex = 1,
        DefaultExt = extension, AddExtension = true, CheckFileExists = true, CheckPathExists = true, Multiselect = false
    };

    public static SaveFileDialog SaveTemplate(string title, string? workingPath = null) => new()
    {
        Title = title, Filter = Filter("AVA-Vorlage", ".json"), FilterIndex = 1,
        DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, CheckPathExists = true,
        FileName = workingPath == null ? "AVA-Vorlage.json" : Path.GetFileName(workingPath)
    };
}

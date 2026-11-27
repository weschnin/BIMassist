using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;
using BIMassist.Commands;
using BIMassist.Views;

namespace BIMassist.Ava.Revit;

/// <summary>Opens a read-only presentation of the latest persisted project run.</summary>
internal static class AvaQuantityRunWindowHost
{
    private static AvaQuantityRunWindow? currentWindow;

    public static void Show(UIApplication application, Document document, AvaQuantityRun run)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(run);
        // The caller reads this snapshot from this document's Extensible Storage.
        // Captured paths are provenance, not identity: first Save / Save As must remain supported.
        currentWindow?.Close();

        var includedIds = new HashSet<long>();
        foreach (var record in run.Records)
        {
            if (string.IsNullOrWhiteSpace(record.RevitElementId)) continue;
            if (!long.TryParse(record.RevitElementId, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var id))
                throw new InvalidDataException($"Ungültige Revit-ElementID im gespeicherten Lauf: {record.RevitElementId}");
            includedIds.Add(id);
        }

        var handler = new AvaX31ReviewCommand.X31ReviewExternalEventHandler(document, includedIds, run);
        var externalEvent = ExternalEvent.Create(handler);
        try
        {
            AvaQuantityRunWindow? window = null;
            window = new AvaQuantityRunWindow(run, [],
                (elementIds, isolate) => Post(() =>
                    handler.SetRequest(new X31SelectionPlan(elementIds, SelectionIncomplete: false), isolate)),
                (useRevitElementId, _) => Post(() => handler.SetX31Request(useRevitElementId)),
                () => Post(handler.SetX82Request), _ => { }, visibleOnly => Post(() => handler.SetRecalculateRequest(visibleOnly)),
                actionsEnabled: true,
                selectionActionsEnabled: true, x31ExportEnabled: true, x82ExportEnabled: true);
            handler.RunWindow = window;
            void Post(Action setRequest)
            {
                var queued = false;
                try
                {
                    setRequest();
                    queued = true;
                    if (externalEvent.Raise() != ExternalEventRequest.Accepted)
                    {
                        handler.CancelRequest();
                        window?.ShowStatus("Revit hat die Anforderung nicht angenommen. Bitte erneut versuchen.");
                    }
                }
                catch (Exception ex)
                {
                    if (queued) handler.CancelRequest();
                    window?.ShowStatus("Die Anforderung konnte nicht gesendet werden: " + ex.Message);
                }
            }
            window.Closed += (_, _) =>
            {
                handler.RunWindow = null;
                handler.CancelRequest();
                if (ReferenceEquals(currentWindow, window)) currentWindow = null;
                externalEvent.Dispose();
            };
            WpfOwner.ShowModeless(window, application);
            currentWindow = window;
        }
        catch
        {
            externalEvent.Dispose();
            throw;
        }
    }

}

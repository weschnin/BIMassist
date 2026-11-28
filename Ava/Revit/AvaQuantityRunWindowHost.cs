using System;
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
    private static AvaX31ReviewCommand.X31ReviewExternalEventHandler? currentHandler;

    public static void Show(UIApplication application, Document document, AvaQuantityRun run)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(run);
        if (currentWindow != null && currentHandler?.IsForDocument(document) == true)
        {
            currentHandler.UpdateRun(run);
            currentWindow.SetRun(run);
            currentWindow.Activate();
            return;
        }
        // The caller reads this snapshot from this document's Extensible Storage.
        // Captured paths are provenance, not identity: first Save / Save As must remain supported.
        currentWindow?.Close();

        var handler = new AvaX31ReviewCommand.X31ReviewExternalEventHandler(document, new HashSet<long>());
        handler.UpdateRun(run);
        var externalEvent = ExternalEvent.Create(handler);
        AvaQuantityViewSettingsSession? settingsSession = null;
        AvaFileViewerSession? fileViewers = null;
        try
        {
            AvaQuantityRunWindow? window = null;
            var settings = AvaQuantityViewSettingsStorage.Load(document);
            settingsSession = new AvaQuantityViewSettingsSession(document, message =>
            {
                if (message == AvaQuantityViewSettingsSession.SavedMessage) return;
                if (window?.IsVisible == true) window.ShowStatus(message);
                else System.Windows.MessageBox.Show(message, "AVA – Fenster-Einstellungen",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            });
            window = new AvaQuantityRunWindow(run, [],
                (elementIds, isolate) => Post(() =>
                    handler.SetRequest(new X31SelectionPlan(elementIds, SelectionIncomplete: false), isolate)),
                (useRevitElementId, _) => Post(() => handler.SetX31Request(useRevitElementId)),
                () => Post(handler.SetX82Request), _ => { }, visibleOnly => Post(() => handler.SetRecalculateRequest(visibleOnly)),
                actionsEnabled: true,
                selectionActionsEnabled: true, x31ExportEnabled: true, x82ExportEnabled: true,
                settings: settings, postSettings: state =>
                {
                    try { settingsSession.Queue(state); }
                    catch (Exception ex) { window?.ShowStatus("Fenster-Einstellungen konnten nicht vorgemerkt werden: " + ex.Message); }
                },
                postMissing: isolate => Post(() => handler.SetMissingRequest(isolate)),
                postExcel: table => Post(() => handler.SetExcelRequest(table)),
                openX31Viewer: () => fileViewers?.OpenX31(),
                openX82Viewer: () => fileViewers?.OpenX82());
            fileViewers = new AvaFileViewerSession(window, window.ShowStatus,
                templateSnapshot: () => window.CurrentRun.TemplateJson);
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
                settingsSession.Close();
                if (ReferenceEquals(currentWindow, window))
                {
                    currentWindow = null;
                    currentHandler = null;
                }
                externalEvent.Dispose();
            };
            WpfOwner.ShowModeless(window, application);
            currentWindow = window;
            currentHandler = handler;
        }
        catch
        {
            fileViewers?.Dispose();
            settingsSession?.Close();
            externalEvent.Dispose();
            throw;
        }
    }

}

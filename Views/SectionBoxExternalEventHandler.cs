using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using System;

namespace BIMassist.Views
{
    internal enum SectionBoxActionType
    {
        Apply,
        Rename,
        Delete
    }

    internal class SectionBoxAction
    {
        public SectionBoxActionType Type;
        public ElementId StorageId;
        public string NewName;
        public Schema Schema;
        public string StatusMessage;

        // Apply-Daten
        public BoundingBoxXYZ Box;
        public XYZ EyePosition;
        public XYZ ForwardDirection;
        public XYZ UpDirection;
    }

    internal class SectionBoxExternalEventHandler : IExternalEventHandler
    {
        public UIApplication UiApp { get; set; }
        public Document Doc { get; set; }
        public Action<string> SetStatus { get; set; }

        public SectionBoxAction Pending { get; set; }

        public string GetName() => "BIMassist SectionBox ExternalEvent";

        public void Execute(UIApplication app)
        {
            if (Pending == null)
                return;

            try
            {
                UiApp = app;
                Doc = app?.ActiveUIDocument?.Document;

                if (UiApp == null || Doc == null)
                    throw new InvalidOperationException("Keine aktive Revit-Session gefunden.");

                switch (Pending.Type)
                {
                    case SectionBoxActionType.Apply:
                        Apply();
                        SetStatus?.Invoke("Snapshot angewendet.");
                        break;

                    case SectionBoxActionType.Rename:
                        Rename();
                        SetStatus?.Invoke("Umbenannt.");
                        break;

                    case SectionBoxActionType.Delete:
                        Delete();
                        SetStatus?.Invoke("Gelöscht.");
                        break;

                    default:
                        throw new InvalidOperationException("Unbekannter Aktionstyp.");
                }
            }
            catch (Exception ex)
            {
                SetStatus?.Invoke("Fehler: " + ex.Message);
            }
            finally
            {
                Pending = null;
            }
        }

        private void Apply()
        {
            UIDocument uiDoc = UiApp.ActiveUIDocument;
            if (uiDoc == null)
                throw new InvalidOperationException("Keine aktive Revit-Ansicht gefunden.");

            View3D view3D = uiDoc.ActiveView as View3D;
            if (view3D == null || view3D.IsTemplate)
                throw new InvalidOperationException("Bitte zuerst eine passende 3D-Ansicht aktivieren.");

            if (Pending.Box == null)
                throw new InvalidOperationException("Keine gespeicherte SectionBox vorhanden.");

            if (Pending.EyePosition == null || Pending.ForwardDirection == null || Pending.UpDirection == null)
                throw new InvalidOperationException("Die gespeicherte Ansichtsorientierung ist unvollständig.");

            using (Transaction tx = new Transaction(Doc, "Restore SectionBox Snapshot"))
            {
                tx.Start();

                view3D.IsSectionBoxActive = true;
                view3D.SetSectionBox(Pending.Box);

                ViewOrientation3D orientation = new ViewOrientation3D(
                    Pending.EyePosition,
                    Pending.UpDirection,
                    Pending.ForwardDirection);

                view3D.SetOrientation(orientation);

                tx.Commit();
            }

            uiDoc.RefreshActiveView();
        }

        private void Rename()
        {
            if (Pending.StorageId == null || Pending.StorageId == ElementId.InvalidElementId)
                throw new InvalidOperationException("Keine gültige StorageId vorhanden.");

            if (Pending.Schema == null)
                throw new InvalidOperationException("Kein Schema vorhanden.");

            if (string.IsNullOrWhiteSpace(Pending.NewName))
                throw new InvalidOperationException("Der neue Name ist leer.");

            DataStorage ds = Doc.GetElement(Pending.StorageId) as DataStorage;
            if (ds == null)
                throw new InvalidOperationException("DataStorage nicht gefunden.");

            using (Transaction tx = new Transaction(Doc, "SectionBox umbenennen"))
            {
                tx.Start();

                Entity e = ds.GetEntity(Pending.Schema);
                if (!e.IsValid())
                    throw new InvalidOperationException("Ungültige Entity im DataStorage.");

                BIMassist.Core.DataStorageManagement.TrySet(e, "Name", Pending.NewName);
                ds.SetEntity(e);

                tx.Commit();
            }
        }

        private void Delete()
        {
            if (Pending.StorageId == null || Pending.StorageId == ElementId.InvalidElementId)
                throw new InvalidOperationException("Keine gültige StorageId vorhanden.");

            using (Transaction tx = new Transaction(Doc, "SectionBox löschen"))
            {
                tx.Start();
                Doc.Delete(Pending.StorageId);
                tx.Commit();
            }
        }
    }
}
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist
{
    public enum AssignMode
    {
        ProjectSelection,
        ActiveFamilyDoc
    }

    public class BgkAssignRequest
    {
        public AssignMode Mode { get; set; }
        public Document TargetDocument { get; set; }
        public List<ElementId> ElementIds { get; set; }
        public string BgkKey { get; set; }
        public string BgkDescription { get; set; }
        public string TypNummer { get; set; }
        public string Typbeschreibung { get; set; }
        public bool BestaetigungBGKZUweisung { get; set; }
        public bool FamilyAutoSave { get; set; }
    }

    public class BgkAssignExternalEventHandler : IExternalEventHandler
    {
        private BgkAssignRequest _req;

        public Action<string> Report { get; set; }

        public bool TrySetRequest(BgkAssignRequest req)
        {
            if (_req != null) return false;
            _req = req;
            return true;
        }

        public void CancelRequest(BgkAssignRequest req)
        {
            if (ReferenceEquals(_req, req)) _req = null;
        }

        public string GetName() => "BGK Manager – Assign Parameters";

        public void Execute(UIApplication app)
        {
            if (_req == null) return;

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null || _req.TargetDocument == null || !_req.TargetDocument.IsValidObject
                || !_req.TargetDocument.Equals(doc))
            {
                Report?.Invoke("Das aktive Dokument wurde gewechselt. Keine BGK-Daten übertragen.");
                _req = null;
                return;
            }

            try
            {
                int assigned;
                if (_req.Mode == AssignMode.ActiveFamilyDoc && doc.IsFamilyDocument)
                {
                    AssignInActiveFamilyDoc(doc);
                    assigned = 1;
                }
                else if (_req.Mode == AssignMode.ProjectSelection && !doc.IsFamilyDocument)
                {
                    var ids = _req.ElementIds ?? new List<ElementId>();
                    if (ids.Count == 0)
                    {
                        ids = uidoc.Selection.GetElementIds().ToList();
                        if (ids.Count == 0)
                            ids = uidoc.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element,
                                "Elemente für BGK-Übertragung wählen und mit ENTER bestätigen")
                                .Select(reference => reference.ElementId).Distinct().ToList();
                    }
                    assigned = AssignInProjectSelection(doc, ids);
                }
                else throw new InvalidOperationException("BGK-Übertragung im aktuellen Dokumentmodus nicht möglich.");

                if (assigned == 0)
                    Report?.Invoke("Keine gültigen Elemente gefunden – keine BGK-Daten übertragen.");
                else
                {
                    Report?.Invoke($"BGK-Daten auf {assigned} Element(e) übertragen.");
                    if (_req.BestaetigungBGKZUweisung)
                        TaskDialog.Show("BGK Manager", $"Die Zuweisung auf {assigned} Element(e) wurde durchgeführt.");
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                Report?.Invoke("Auswahl abgebrochen – keine BGK-Daten übertragen.");
            }
            catch (Exception ex)
            {
                Report?.Invoke("BGK-Übertragung fehlgeschlagen (ggf. teilweise): " + ex.Message);
            }
            finally
            {
                _req = null; // Request consumed
            }
        }

        #region Implementation

        private int AssignInProjectSelection(Document doc, IList<ElementId> elementIds)
        {
            int assigned = 0;
            foreach (var id in elementIds.Distinct())
            {
                Element el = doc.GetElement(id);
                if (el == null) continue;

                // If family instance -> write in editable family
                var fam = (el as FamilyInstance)?.Symbol?.Family;
                if (fam != null && fam.IsEditable)
                {
                    WriteInEditableFamily(el, doc);
                    assigned++;
                }
                else
                {
                    if (WriteOnTypeParameters(el, doc)) assigned++;
                }
            }
            return assigned;
        }

        private bool WriteOnTypeParameters(Element selEl, Document dc)
        {
            var elTyp = dc.GetElement(selEl.GetTypeId()) as ElementType;
            if (elTyp == null) return false;

            var paramTypenmarkierung = elTyp.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
            var paramTypModell = elTyp.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
            var paramTypbeschreibung = elTyp.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

            using (Transaction tx = new Transaction(dc, "BGK und Typenmarkierung zuweisen"))
            {
                tx.Start();

                SetString(paramTypenmarkierung, $"{_req.BgkKey} {_req.BgkDescription}", "Baugruppencode");
                SetString(paramTypModell, _req.TypNummer, "Typkommentare");
                SetString(paramTypbeschreibung, _req.Typbeschreibung, "Beschreibung");

                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit hat die BGK-Typwerte nicht gespeichert.");
            }
            return true;
        }

        private static void SetString(Parameter parameter, string value, string name)
        {
            value ??= string.Empty;
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String
                || (!string.Equals(parameter.AsString() ?? string.Empty, value, StringComparison.Ordinal)
                    && !parameter.Set(value)))
                throw new InvalidOperationException($"Der Typparameter '{name}' konnte nicht beschrieben werden.");
        }

        private static void SetFamilyString(FamilyManager manager, FamilyParameter parameter, string value, string name)
        {
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                throw new InvalidOperationException($"Der Familientypparameter '{name}' ist nicht bearbeitbar.");
            manager.Set(parameter, value ?? string.Empty);
        }

        private void WriteInEditableFamily(Element selectedElement, Document projectDoc)
        {
            var famIns = selectedElement as FamilyInstance;
            if (famIns == null) throw new InvalidOperationException("Das ausgewählte Element ist keine ladbare Familie.");

            Document famDoc = projectDoc.EditFamily(famIns.Symbol.Family);
            if (famDoc == null) throw new InvalidOperationException("Revit konnte die ausgewählte Familie nicht bearbeiten.");

            try
            {
                FamilyManager famManager = famDoc.FamilyManager;

                // activate matching FamilyType by name
                var elTyp = projectDoc.GetElement(selectedElement.GetTypeId()) as ElementType;
                if (elTyp == null) throw new InvalidOperationException("Der Familientyp wurde nicht gefunden.");

                using (Transaction tx = new Transaction(famDoc, "BGK-Parameter verifizieren und zuweisen"))
                {
                    tx.Start();

                    bool foundType = false;
                    foreach (FamilyType t in famManager.Types)
                        if (t.Name == elTyp.Name)
                        {
                            famManager.CurrentType = t;
                            foundType = true;
                            break;
                        }
                    if (!foundType)
                        throw new InvalidOperationException("Der gewählte Typ fehlt im Familiendokument.");

                    var parameterBGK = famManager.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                    var paramTypnummer = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                    var paramDesc = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

                    SetFamilyString(famManager, parameterBGK, $"{_req.BgkKey} {_req.BgkDescription}", "Baugruppencode");
                    SetFamilyString(famManager, paramTypnummer, _req.TypNummer, "Typkommentare");
                    SetFamilyString(famManager, paramDesc, _req.Typbeschreibung, "Beschreibung");

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit hat die BGK-Familientypwerte nicht gespeichert.");
                }

                if (_req.FamilyAutoSave && !string.IsNullOrEmpty(famDoc.PathName))
                    famDoc.Save();

                // reload family
                if (famDoc.LoadFamily(projectDoc, new JtFamilyLoadOptions()) == null)
                    throw new InvalidOperationException("Die Familie konnte nicht ins Projekt zurückgeladen werden.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("BGK-Übertragung in die Familie fehlgeschlagen: " + ex.Message, ex);
            }
            finally
            {
                famDoc.Close(false);
            }
        }

        private void AssignInActiveFamilyDoc(Document famDoc)
        {
            FamilyManager famManager = famDoc.FamilyManager;
            if (famManager == null) throw new InvalidOperationException("Kein Familienmanager im aktiven Dokument.");

            using (Transaction tx = new Transaction(famDoc, "BGK-Parameter verifizieren und zuweisen"))
            {
                tx.Start();
                if (famManager.CurrentType == null)
                {
                    var first = famManager.Types?.Cast<FamilyType>()?.FirstOrDefault();
                    if (first != null) famManager.CurrentType = first;
                }
                if (famManager.CurrentType == null)
                    throw new InvalidOperationException("Kein Familientyp für die BGK-Daten vorhanden.");

                var parameterBGK = famManager.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                var paramTypnummer = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                var paramDesc = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

                SetFamilyString(famManager, parameterBGK, $"{_req.BgkKey} {_req.BgkDescription}", "Baugruppencode");
                SetFamilyString(famManager, paramTypnummer, _req.TypNummer, "Typkommentare");
                SetFamilyString(famManager, paramDesc, _req.Typbeschreibung, "Beschreibung");

                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit hat die BGK-Familientypwerte nicht gespeichert.");
            }
        }

        #endregion
    }
}

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

        public void SetRequest(BgkAssignRequest req) => _req = req;

        public string GetName() => "BGK Manager – Assign Parameters";

        public void Execute(UIApplication app)
        {
            if (_req == null) return;

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return;

            try
            {
                if (_req.Mode == AssignMode.ActiveFamilyDoc && doc.IsFamilyDocument)
                {
                    AssignInActiveFamilyDoc(doc);
                }
                else
                {
                    AssignInProjectSelection(uidoc, doc);
                }

                if (_req.BestaetigungBGKZUweisung)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("AWESBox", "Die Zuweisung wurde erfolgreich durchgeführt.");
                }
            }
            catch (Exception)
            {
                // optionally log
            }
            finally
            {
                _req = null; // Request consumed
            }
        }

        #region Implementation

        private void AssignInProjectSelection(UIDocument uidoc, Document doc)
        {
            if (_req.ElementIds == null || _req.ElementIds.Count == 0) return;

            foreach (var id in _req.ElementIds)
            {
                Element el = doc.GetElement(id);
                if (el == null) continue;

                // If family instance -> write in editable family
                var fam = (el as FamilyInstance)?.Symbol?.Family;
                if (fam != null && fam.IsEditable)
                {
                    WriteInEditableFamily(el, doc);
                }
                else
                {
                    WriteOnTypeParameters(el, doc);
                }
            }
        }

        private void WriteOnTypeParameters(Element selEl, Document dc)
        {
            var elTyp = dc.GetElement(selEl.GetTypeId()) as ElementType;
            if (elTyp == null) return;

            var paramTypenmarkierung = elTyp.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
            var paramTypModell = elTyp.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
            var paramTypbeschreibung = elTyp.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

            using (Transaction tx = new Transaction(dc, "BGK und Typenmarkierung zuweisen"))
            {
                tx.Start();

                paramTypenmarkierung?.Set($"{_req.BgkKey} {_req.BgkDescription}");
                paramTypModell?.Set(_req.TypNummer);
                paramTypbeschreibung?.Set(_req.Typbeschreibung);

                tx.Commit();
            }
        }

        private void WriteInEditableFamily(Element selectedElement, Document projectDoc)
        {
            var famIns = selectedElement as FamilyInstance;
            if (famIns == null) return;

            Document famDoc = projectDoc.EditFamily(famIns.Symbol.Family);
            if (famDoc == null) return;

            try
            {
                FamilyManager famManager = famDoc.FamilyManager;

                // activate matching FamilyType by name
                var elTyp = projectDoc.GetElement(selectedElement.GetTypeId()) as ElementType;
                if (elTyp == null) return;

                using (Transaction tx = new Transaction(famDoc, "BGK-Parameter verifizieren und zuweisen"))
                {
                    tx.Start();

                    foreach (FamilyType t in famManager.Types)
                        if (t.Name == elTyp.Name)
                            famManager.CurrentType = t;

                    var parameterBGK = famManager.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                    var paramTypnummer = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                    var paramDesc = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

                    if (parameterBGK != null) famManager.Set(parameterBGK, $"{_req.BgkKey} {_req.BgkDescription}");
                    if (paramTypnummer != null) famManager.Set(paramTypnummer, _req.TypNummer);
                    if (paramDesc != null) famManager.Set(paramDesc, _req.Typbeschreibung);

                    tx.Commit();
                }

                if (_req.FamilyAutoSave && !string.IsNullOrEmpty(famDoc.PathName))
                    famDoc.Save();

                // reload family
                famDoc.LoadFamily(projectDoc, new JtFamilyLoadOptions());
            }
            catch (Exception)
            {
                // optional logging
            }
            finally
            {
                famDoc.Close(false);
            }
        }

        private void AssignInActiveFamilyDoc(Document famDoc)
        {
            FamilyManager famManager = famDoc.FamilyManager;
            if (famManager == null) return;

            using (Transaction tx = new Transaction(famDoc, "BGK-Parameter verifizieren und zuweisen"))
            {
                tx.Start();

                var parameterBGK = famManager.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                var paramTypnummer = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                var paramDesc = famManager.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

                if (parameterBGK != null) famManager.Set(parameterBGK, $"{_req.BgkKey} {_req.BgkDescription}");
                if (paramTypnummer != null) famManager.Set(paramTypnummer, _req.TypNummer);
                if (paramDesc != null) famManager.Set(paramDesc, _req.Typbeschreibung);

                tx.Commit();
            }
        }

        #endregion
    }
}

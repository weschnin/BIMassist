using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMassist.Core
{
    internal static class FamilyGeometryTools
    {
        internal static IList<Solid> ReadGeometryFromFamily(Document doc, IList<ElementId> solidsElementIds, bool vereinigung)
        {
            Options opt = new Options() { View = doc.ActiveView, ComputeReferences = true };
            IList<Solid> solids = new List<Solid>();

            foreach (ElementId elementIds in solidsElementIds)
            {
                
                Element element = doc.GetElement(elementIds);
                Element elementTyp = doc.GetElement(element.GetTypeId());

                var l = element.GetMaterialIds(false).ToList();

                var lst = GetAllGeometryObjects(element, opt);

                foreach (var geomObject in lst)
                {
                    if (geomObject is Solid s)
                        if (s.Volume > 0)
                            solids.Add(s);

                    if (geomObject is Mesh mesh)
                    {
                        TessellatedShapeBuilder builder = new TessellatedShapeBuilder();
                        builder.OpenConnectedFaceSet(false);
                        List<XYZ> args = new List<XYZ>(3);

                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            MeshTriangle triangle = mesh.get_Triangle(i);

                            args.Clear();
                            args.Add(triangle.get_Vertex(0));
                            args.Add(triangle.get_Vertex(1));
                            args.Add(triangle.get_Vertex(2));

                            TessellatedFace tesseFace = new TessellatedFace(args, ElementId.InvalidElementId);

                            if (builder.DoesFaceHaveEnoughLoopsAndVertices(tesseFace))
                                builder.AddFace(tesseFace);
                        }
                        try
                        {
                            builder.CloseConnectedFaceSet();

                            builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
                            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
                            builder.Build();

                            TessellatedShapeBuilderResult result = builder.GetBuildResult();

                            if (result.GetGeometricalObjects()[0] is Solid sol)
                                if (sol.Volume > 0)
                                    solids.Add(sol);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                try
                {
                    var unionSolid = solids.Aggregate((a, b) => BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Union));
                }
                catch (Exception msg)
                {
                    TaskDialog.Show("Fehlermeldung", msg.Message);
                }
            }

            if (solids.Count > 0)
            {
                return solids;
            }
            else
                TaskDialog.Show("Fehler", "Es wurden keine Geometrien erkannt");
            return null;
        }
        internal static List<GeometryObject> GetAllGeometryObjects(Element element, Options opt)
        {
            List<GeometryObject> geometryObjects = new List<GeometryObject>();

            GeometryElement geometryElement = element.get_Geometry(opt);

            foreach (var geometryObject in geometryElement)
            {
                if (geometryObject is Solid solid && solid.Volume > 0)
                    if (geometryObjects.Any(obj => obj is Solid && ((Solid)obj).Volume == solid.Volume)) { }
                    else geometryObjects.Add(geometryObject);

                if(geometryObject is Mesh)
                    geometryObjects.Add(geometryObject);


                if (geometryObject is GeometryInstance geometryInstance)
                {
                    foreach (var item in geometryInstance.GetInstanceGeometry())
                    {
                        if (item is Solid soliditem && soliditem.Volume > 0)
                            if (geometryObjects.Any(obj => obj is Solid && ((Solid)obj).Volume == soliditem.Volume)) { }
                            else geometryObjects.Add(item);

                        if (item is Mesh)
                            geometryObjects.Add(item);
                        
                    }
                }
            }

            if (element is FamilyInstance)
            {
                FamilyInstance famInst = element as FamilyInstance;
                    
                var subComponentIds = famInst.GetSubComponentIds();
                    
                foreach (ElementId subComponentId in subComponentIds)
                {
                    Element subComponent = element.Document.GetElement(subComponentId);
                    if (subComponent != null)
                    {
                        // Recursively call the function for each sub-component
                        List<GeometryObject> subComponentGeometryObjects = GetAllGeometryObjects(subComponent, opt);
                        geometryObjects.AddRange(subComponentGeometryObjects);
                    }
                }
            }
            
            return geometryObjects;
        }
        internal static IList<XYZ> ReadGeometryPoints(Document doc, Reference faceReference, double LevelOfDetails)
        {
            IList<XYZ> m_points = new List<XYZ>();
            Face elementFace = doc.GetElement(faceReference).GetGeometryObjectFromReference(faceReference) as Face;
            IList<XYZ> points = elementFace?.Triangulate(LevelOfDetails)?.Vertices;
            foreach (XYZ m_point in points)
                m_points.Add(m_point);

            return m_points;
        }
        internal static async void CreateNewFamily(UIApplication uiapp, Document dc, IList<Solid> solids, string FamName, XYZ position = null)
        {
            RevitTask.Initialize(uiapp);

            await RevitTask.RunAsync(app =>
            {
                string template_path = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
                if (string.IsNullOrEmpty(template_path)) template_path = app.Application.FamilyTemplatePath + "\\Vorlage Allgemeine Familie.rft";

                Document family_doc = null;
                Family fam;
                bool result = true;

                //Überprüfung auf das Vorhandensein der Vorlagendatei

                while (result)
                {
                    try
                    {
                        family_doc = app.Application.NewFamilyDocument(template_path);
                        result = false;
                    }
                    catch (Exception)
                    {
                        TaskDialog.Show("Fehler", "Für die Erstellung einer Familie ist eine Vorlagendatei für Allgemeine Familien erforderlich. " +
                            "Der voreingestellte Pfad für \"Vorlage Allgemeine Familie.rft\" konnte nicht gefunden werden. " +
                            "Bitte die Vorlagendatei auswählen");

                        OpenFileDialog openFileDialog = new OpenFileDialog()
                        {
                            Filter = "rft files (*.rft)|*.rft",
                            InitialDirectory = app.Application.FamilyTemplatePath
                        };

                        if (openFileDialog.ShowDialog() == DialogResult.OK)
                        {
                            template_path = openFileDialog.FileName;
                            Properties.Settings.Default.PfadVorlageAllgemeineFamilie = template_path;
                            Properties.Settings.Default.Save();
                        }
                        else
                            return;
                    }
                }

                // Geometrien einfügen

                FamilyManager fammanage = family_doc?.FamilyManager;

                FreeFormElement freeform;
                Solid solid = null;

                using (Transaction t = new Transaction(family_doc, "Neue Familien mit ausgewählten Geometrien erstellen"))
                {
                    t.Start();

                    fammanage.CurrentType = fammanage?.NewType("Geometrie");

                    foreach (var solidItem in solids)
                                freeform = FreeFormElement.Create(family_doc, solidItem);
                    t.Commit();
                }

                fam = family_doc?.LoadFamily(dc, new JtFamilyLoadOptions());
                family_doc?.Close(false);

                using (Transaction t = new Transaction(dc, "Neue Familien mit ausgewählten Geometrien erstellen"))
                {
                    t.Start();
                    fam.Name = FamName;
                    t.Commit();
                }

                if (!dc.IsFamilyDocument)
                {
                    if (position == null)
                        AddFamilyinstance(dc, fam);
                    else
                        AddFamilyinstance(dc, fam, position);
                }
                else
                    TaskDialog.Show("Fehler", "Die Familie wurde erstellt. Eine Familieninstanz kann jedoch nicht in einem Familiendokument erstellt werden. Bitte Familieinstanz manuell erstellen.");
            });
        }
        internal static async void AddSolidsToFamily(UIApplication uiapp, Document doc, IList<Solid> solids, ElementId targetDocId)
        {
            RevitTask.Initialize(uiapp);

            await RevitTask.RunAsync(app =>
            {
                Document targetfamily_doc = null;
                Family fam;
                FamilyManager targetFammanage = null;
                FreeFormElement freeform;

                Element element = doc.GetElement(targetDocId);
                if (element is FamilyInstance)
                {
                    FamilyInstance familyInstance = element as FamilyInstance;
                    Family family = familyInstance.Symbol.Family;
                    targetfamily_doc = doc.EditFamily(family);
                    targetFammanage = targetfamily_doc?.FamilyManager;
                }

                // Geometrien einfügen

                using (Transaction t = new Transaction(targetfamily_doc, "Neue Familien mit ausgewählten Geometrien erstellen"))
                {
                    t.Start();

                    //targetFammanage.CurrentType = targetFammanage?.NewType("Geometrie");

                    foreach (var solidItem in solids)
                        freeform = FreeFormElement.Create(targetfamily_doc, solidItem);

                    t.Commit();
                }

                fam = targetfamily_doc?.LoadFamily(doc, new JtFamilyLoadOptions());
                targetfamily_doc?.Close(false);
            });
        }
        internal static void AddFamilyinstance(Document docu, Family fam)
        {
            var fsLst = new FilteredElementCollector(docu).WherePasses(new FamilySymbolFilter(fam.Id)).ToElementIds();
            FamilySymbol fs;

            foreach (ElementId item in fsLst)
            {
                fs = docu.GetElement(item) as FamilySymbol;
                Document fdoc = docu.EditFamily(fs.Family);

                //var ellst = new FilteredElementCollector(fdoc).OfClass(typeof(FreeFormElement)).ToElements();

                fdoc.LoadFamily(docu, new JtFamilyLoadOptions());
                fdoc.Close(false);

                FamilyInstance faminst;

                using (Transaction t = new Transaction(docu, "Eine Instanz der neuen Familie im Dokument hinzufügen"))
                {
                    t.Start();
                    if (!fs.IsActive) fs.Activate();
                    try
                    {
                        docu.Create.NewFamilyInstance(new XYZ(), fs, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    }
                    catch (Exception msg)
                    {
                        TaskDialog.Show("Fehler", msg.Message);
                    }

                    t.Commit();
                }
            }
        }
        internal static void AddFamilyinstance(Document dc, Family fam, XYZ position)
        {
            var fsLst = new FilteredElementCollector(dc).WherePasses(new FamilySymbolFilter(fam.Id)).ToElementIds();
            FamilySymbol fs;

            foreach (ElementId item in fsLst)
            {
                fs = dc.GetElement(item) as FamilySymbol;
                Document fdoc = dc.EditFamily(fs.Family);

                //var ellst = new FilteredElementCollector(fdoc).OfClass(typeof(FreeFormElement)).ToElements();

                fdoc.LoadFamily(dc, new JtFamilyLoadOptions());
                fdoc.Close(false);

                FamilyInstance faminst;

                using (Transaction t = new Transaction(dc, "Eine Instanz der neuen Familie im Dokument hinzufügen"))
                {
                    t.Start();
                    if (!fs.IsActive) fs.Activate();

                    faminst = dc.Create.NewFamilyInstance(position, fs, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    t.Commit();
                }
            }
        }
        
    }
}

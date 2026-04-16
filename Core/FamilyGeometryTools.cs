using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using System.Windows.Controls;

namespace BIMassist.Core
{
    /// <summary>
    /// Hilfsklasse zur Verarbeitung und Erstellung von Familien-Geometrien in Revit.
    /// Enthält Methoden zum Auslesen, Umwandeln, Vereinen und Einfügen von 3D-Geometrien in Familien.
    /// </summary>
    internal static class FamilyGeometryTools
    {
        /// <summary>
        /// Liest die Geometrie-Objekte (Solids) einer Liste von Elementen aus einer Familie aus.
        /// Optional können die gefundenen Solids zu einem einzigen Solid vereinigt werden.
        /// </summary>
        /// <param name="doc">Das aktuelle Revit-Dokument.</param>
        /// <param name="solidsElementIds">Liste der ElementIds, aus denen Geometrie ausgelesen werden soll.</param>
        /// <param name="vereinigung">Gibt an, ob die gefundenen Solids vereinigt werden sollen (Union).</param>
        /// <returns>Liste der ausgelesenen Solids, oder null bei Fehler.</returns>
        internal static IList<GeometryObject> ReadGeometryFromFamily(Document doc, IList<ElementId> solidsElementIds)
        {
            // Optionen für die Geometrie-Auslese im aktuellen View
            Options opt = new Options() { View = doc.ActiveView, ComputeReferences = true };
            List<GeometryObject> geometryObjs = new List<GeometryObject>();
            int nichtÜbernommeneMeshes = 0;

            foreach (ElementId elementId in solidsElementIds)
            {
                Element element = doc.GetElement(elementId);

                // Geometrieobjekte (Solids & Meshes) des Elements auslesen
                var geometryObjects = GetAllGeometryObjects(element, opt);

                foreach (var geomObject in geometryObjects)
                {
                    // Direktes Hinzufügen von Solids mit Volumen > 0
                    if (geomObject is Solid s && s.Volume > 0)
                    {
                        geometryObjs.Add(s);
                    }
                    // Umwandlung von Mesh zu Solid via TessellatedShapeBuilder
                    else if (geomObject is Mesh mesh)
                    {
                        bool solidErzeugt = false;

                        try
                        {
                            TessellatedShapeBuilder builder = new TessellatedShapeBuilder();
                            builder.OpenConnectedFaceSet(false);

                            // Alle Mesh-Triangles als TessellatedFace hinzufügen
                            for (int i = 0; i < mesh.NumTriangles; i++)
                            {
                                MeshTriangle triangle = mesh.get_Triangle(i);
                                var vertices = new List<XYZ>
                            {
                                triangle.get_Vertex(0),
                                triangle.get_Vertex(1),
                                triangle.get_Vertex(2)
                            };

                                var tessFace = new TessellatedFace(vertices, ElementId.InvalidElementId);
                                if (builder.DoesFaceHaveEnoughLoopsAndVertices(tessFace))
                                    builder.AddFace(tessFace);
                            }

                            builder.CloseConnectedFaceSet();
                            builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
                            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
                            builder.Build();

                            TessellatedShapeBuilderResult result = builder.GetBuildResult();
                            Solid solid = result.GetGeometricalObjects().FirstOrDefault() as Solid;
                            if (solid != null && solid.Volume > 0)
                            {
                                geometryObjs.Add(solid);
                                solidErzeugt = true;
                            }
                        }
                        catch
                        {
                            // Fehler ignorieren
                        }

                        if (!solidErzeugt && mesh.NumTriangles > 0)
                        {
                            nichtÜbernommeneMeshes++;
                        }
                    }
                }
            }

            // User-Meldung, falls Meshes nicht in Solid umwandelbar sind
            if (nichtÜbernommeneMeshes > 0)
            {
                TaskDialog.Show("Nicht geschlossene Meshes erkannt",
                    $"Es wurden {nichtÜbernommeneMeshes} Mesh-Objekt(e) gefunden, die NICHT zu einem Volumenkörper (Solid) konvertiert werden konnten.\n\n" +
                    "Diese können NICHT automatisch in die Familie übernommen werden!\n\n" +
                    "Tipp: Reparieren Sie das Mesh (z.B. mit MeshLab, Blender, Netfabb), sodass es geschlossen ist, und importieren Sie es dann erneut."
                );
            }

            if (geometryObjs.Count > 0)
                return geometryObjs;
            else
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Es wurden keine (gültigen) Geometrien erkannt");
                return null;
            }
        }

        /// <summary>
        /// Liefert alle Geometrieobjekte (Solids, Meshes, Instanzen und Sub-Komponenten) eines Elements rekursiv zurück.
        /// NEU: Geht auch beliebig tief in verschachtelte GeometryInstance (z.B. Blockreferenzen aus CAD).
        /// </summary>
        internal static List<GeometryObject> GetAllGeometryObjects(Element element, Options opt)
        {
            List<GeometryObject> geometryObjects = new List<GeometryObject>();
            GeometryElement geometryElement = element.get_Geometry(opt);

            if (geometryElement == null)
                return geometryObjects;

            // --- LOKALE rekursive Funktion, damit du beliebig tief in GeometryInstances (Blocks) gehst ---
            void ExtractRecursive(GeometryElement geoElem)
            {
                foreach (var geometryObject in geoElem)
                {
                    if (geometryObject is Solid solid && solid.Volume > 0)
                    {
                        geometryObjects.Add(solid);
                    }
                    else if (geometryObject is Mesh mesh)
                    {
                        geometryObjects.Add(mesh);
                    }
                    else if (geometryObject is GeometryInstance geometryInstance)
                    {
                        // REKURSION für weitere Blockreferenzen/Unterfamilien
                        ExtractRecursive(geometryInstance.GetInstanceGeometry());
                    }
                }
            }

            ExtractRecursive(geometryElement);

            // Zusätzlich: Für FamilyInstance auch Sub-Komponenten rekursiv durchsuchen (wie gehabt)
            if (element is FamilyInstance famInst)
            {
                foreach (ElementId subComponentId in famInst.GetSubComponentIds())
                {
                    Element subComponent = element.Document.GetElement(subComponentId);
                    if (subComponent != null)
                        geometryObjects.AddRange(GetAllGeometryObjects(subComponent, opt));
                }
            }

            return geometryObjects;
        }

        /// <summary>
        /// Erzeugt eine Punktwolke (Liste von XYZ) aus einer Face-Referenz, trianguliert mit gewünschtem Detaillierungsgrad.
        /// </summary>
        internal static IList<XYZ> ReadGeometryPoints(Document doc, Reference faceReference, double levelOfDetails)
        {
            IList<XYZ> m_points = new List<XYZ>();
            Face elementFace = doc.GetElement(faceReference).GetGeometryObjectFromReference(faceReference) as Face;

            // Prüfen ob Face existiert
            if (elementFace == null) return m_points;

            IList<XYZ> points = elementFace.Triangulate(levelOfDetails)?.Vertices;

            if (points != null)
            {
                foreach (XYZ pt in points)
                    m_points.Add(pt);
            }
            return m_points;
        }

        /// <summary>
        /// Erstellt eine neue Familien-Datei mit den übergebenen Solids und speichert diese.
        /// Familieninstanz wird ggf. direkt im aktiven Dokument platziert.
        /// </summary>
        internal static void CreateNewFamily(UIApplication uiapp, Document dc, IList<GeometryObject> geos, string famName, XYZ position = null)
        {
            // Standard-Vorlagendatei für Familien bestimmen
            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrEmpty(templatePath))
                templatePath = uiapp.Application.FamilyTemplatePath + "\\Allgemeine Familie.rft";

            Document familyDoc = null;
            Family family;

            // Vorlagen-Datei laden/auswählen falls nicht vorhanden
            while (familyDoc == null)
            {
                try
                {
                    familyDoc = uiapp.Application.NewFamilyDocument(templatePath);
                }
                catch
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Vorlagendatei 'Allgemeine Familie.rft' konnte nicht gefunden werden. Bitte wählen Sie die Datei aus.");

                    Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog()
                    {
                        Filter = "rft files (*.rft)|*.rft",
                        InitialDirectory = uiapp.Application.FamilyTemplatePath
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        templatePath = openFileDialog.FileName;
                        Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                        Properties.Settings.Default.Save();
                    }
                    else
                        return; // Abbruch durch Nutzer
                }
            }

            // Geometrien einfügen
            FamilyManager fammanage = familyDoc.FamilyManager;
            using (Transaction t = new Transaction(familyDoc, "Neue Familie mit Geometrien erstellen"))
            {
                t.Start();

                FamilyType famType = fammanage.Types.Size > 0
                    ? fammanage.Types.Cast<FamilyType>().First()
                    : fammanage.NewType("Geometrie");

                fammanage.CurrentType = famType;

                foreach (var obj in geos)
                    if (obj is Solid solid)
                        FreeFormElement.Create(familyDoc, solid);

                t.Commit();
            }

            // Familie ins Projekt laden
            family = familyDoc.LoadFamily(dc, new JtFamilyLoadOptions());
            familyDoc.Close(false);

            // Familie umbenennen
            using (Transaction t = new Transaction(dc, "Familienname anpassen"))
            {
                t.Start();
                family.Name = famName;
                t.Commit();
            }

            // Familie im Dokument platzieren, falls kein FamilyDoc
            if (!dc.IsFamilyDocument)
            {
                if (position == null)
                    AddFamilyinstance(dc, family);
                else
                    AddFamilyinstance(dc, family, position);
            }
            else
            {
                Autodesk.Revit.UI.TaskDialog.Show("Hinweis", "Familie wurde erstellt. Instanziierung im Familien-Dokument ist nicht möglich.");
            }
        }

        /// <summary>
        /// Fügt eine oder mehrere Solids zu einer bestehenden Familien-Dokumentdatei hinzu.
        /// </summary>
        internal static void AddSolidsToFamily(UIApplication uiapp, Document doc, IList<GeometryObject> geos, ElementId targetDocId)
        {
            Document targetFamilyDoc = null;
            FamilyManager targetFamManager = null;

            Element element = doc.GetElement(targetDocId);
            if (element is FamilyInstance familyInstance)
            {
                Family family = familyInstance.Symbol.Family;
                targetFamilyDoc = doc.EditFamily(family);
                targetFamManager = targetFamilyDoc.FamilyManager;
            }

            if (targetFamilyDoc == null) return;

            using (Transaction t = new Transaction(targetFamilyDoc, "Geometrien zu bestehender Familie hinzufügen"))
            {
                t.Start();

                foreach (var obj in geos)
                {
                    if (obj is Solid solid)
                        FreeFormElement.Create(targetFamilyDoc, solid);
                    // Meshes können NICHT eingefügt werden (siehe vorherige Antwort)
                    // else if (obj is Mesh mesh)
                    //     FreeFormElement.Create(targetFamilyDoc, mesh); // --> funktioniert NICHT in der Revit API!
                }

                t.Commit();
            }

            targetFamilyDoc.LoadFamily(doc, new JtFamilyLoadOptions());
            targetFamilyDoc.Close(false);
        }

        /// <summary>
        /// Erstellt eine neue Instanz der Familie im aktuellen Projekt-Dokument (an zufälliger Position).
        /// </summary>
        internal static void AddFamilyinstance(Document docu, Family fam)
        {
            var fsLst = new FilteredElementCollector(docu)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => fs.Family.Id == fam.Id && fs.Name == "Geometrie") // nur Typ „Geometrie“
                .ToList();

            if (fsLst.Count == 0)
            {
                TaskDialog.Show("Fehler", "Kein FamilySymbol mit Namen 'Geometrie' gefunden!");
                return;
            }

            FamilySymbol fs = fsLst.First();

            using (Transaction t = new Transaction(docu, "Familieninstanz 'Geometrie' platzieren"))
            {
                t.Start();
                if (!fs.IsActive) fs.Activate();
                docu.Create.NewFamilyInstance(new XYZ(), fs, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                t.Commit();
            }
        }

        /// <summary>
        /// Erstellt eine neue Instanz der Familie an einer definierten Position.
        /// </summary>
        internal static void AddFamilyinstance(Document docu, Family fam, XYZ position)
        {
            var symbolIds = new FilteredElementCollector(docu)
                .WherePasses(new FamilySymbolFilter(fam.Id))
                .ToElementIds();

            foreach (ElementId id in symbolIds)
            {
                FamilySymbol symbol = docu.GetElement(id) as FamilySymbol;
                if (symbol == null)
                    continue;

                // NUR wenn der Typ-Name "Geometrie" ist:
                if (symbol.Name != "Geometrie")
                    continue;

                using (Transaction t = new Transaction(docu, "Familieninstanz 'Geometrie' platzieren"))
                {
                    t.Start();
                    if (!symbol.IsActive) symbol.Activate();

                    // Beispiel: an Ursprung platzieren, kann natürlich positioniert werden!
                    docu.Create.NewFamilyInstance(new XYZ(), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    t.Commit();
                }
            }
        }

        /// <summary>
        /// Erstellt eine neue Familie mit dem Volumenkörper als ABZUGSKÖRPER.
        /// Die Option "Abzugskörper beim Laden in Projekt schneiden" wird aktiviert.
        /// </summary>
        /// <param name="uiapp">UIApplication</param>
        /// <param name="doc">Das aktuelle Projekt-Dokument</param>
        /// <param name="solid">Das Solid das als Abzugskörper verwendet werden soll</param>
        /// <param name="famName">Name der neuen Familie</param>
        /// <param name="position">Position für die Platzierung (optional) - wenn null, wird das Solid an seiner Original-Position belassen</param>
        /// <returns>Die erstellte FamilyInstance oder null bei Fehler</returns>
        internal static FamilyInstance CreateVoidFamily(UIApplication uiapp, Document doc, Solid solid, string famName, XYZ position = null)
        {
            // Standard-Vorlagendatei für Familien bestimmen
            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrEmpty(templatePath))
                templatePath = uiapp.Application.FamilyTemplatePath + "\\Allgemeine Familie.rft";

            Document familyDoc = null;
            Family family = null;

            // Vorlagen-Datei laden/auswählen falls nicht vorhanden
            while (familyDoc == null)
            {
                try
                {
                    familyDoc = uiapp.Application.NewFamilyDocument(templatePath);
                }
                catch
                {
                    TaskDialog.Show("Fehler", "Vorlagendatei 'Allgemeine Familie.rft' konnte nicht gefunden werden. Bitte wählen Sie die Datei aus.");

                    Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog()
                    {
                        Filter = "rft files (*.rft)|*.rft",
                        InitialDirectory = uiapp.Application.FamilyTemplatePath
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        templatePath = openFileDialog.FileName;
                        Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                        Properties.Settings.Default.Save();
                    }
                    else
                        return null; // Abbruch durch Nutzer
                }
            }

            FreeFormElement voidElement = null;

            // Geometrie als Abzugskörper einfügen
            // WICHTIG: Das Solid wird NICHT verschoben - es behält seine Original-Koordinaten
            // Die Familie wird dann bei (0,0,0) platziert, sodass das Solid an seiner Original-Position erscheint
            using (Transaction t = new Transaction(familyDoc, "Abzugskörper erstellen"))
            {
                t.Start();

                FamilyManager famManager = familyDoc.FamilyManager;

                // Familientyp erstellen/verwenden
                FamilyType famType = famManager.Types.Size > 0
                    ? famManager.Types.Cast<FamilyType>().First()
                    : famManager.NewType("Bodenverdrängung");

                famManager.CurrentType = famType;

                // FreeFormElement erstellen - Solid behält seine Original-Koordinaten
                voidElement = FreeFormElement.Create(familyDoc, solid);

                if (voidElement != null)
                {
                    // Als Abzugskörper (Void) setzen
                    Parameter isSolidParam = voidElement.get_Parameter(BuiltInParameter.ELEMENT_IS_CUTTING);
                    if (isSolidParam != null && !isSolidParam.IsReadOnly)
                    {
                        isSolidParam.Set(1); // 1 = Abzugskörper (Void), 0 = Volumenkörper (Solid)
                    }
                }

                t.Commit();
            }

            // "Abzugskörper beim Laden schneiden" aktivieren
            using (Transaction t = new Transaction(familyDoc, "Abzugskörper-Option aktivieren"))
            {
                t.Start();

                // Familienparameter für "Beim Laden in Projekt schneiden" setzen
                FamilyManager famManager = familyDoc.FamilyManager;

                // Diese Option wird über den FamilyManager gesetzt
                // Parameter: "Schneidet mit Abzugskörper beim Laden"
                try
                {
                    Parameter cutWithVoidsParam = familyDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
                    if (cutWithVoidsParam != null && !cutWithVoidsParam.IsReadOnly)
                    {
                        cutWithVoidsParam.Set(1);
                    }
                }
                catch { }

                t.Commit();
            }

            // Familie ins Projekt laden
            family = familyDoc.LoadFamily(doc, new JtFamilyLoadOptions());
            familyDoc.Close(false);

            if (family == null)
            {
                TaskDialog.Show("Fehler", "Familie konnte nicht in das Projekt geladen werden.");
                return null;
            }

            // Familie umbenennen
            FamilyInstance placedInstance = null;

            using (Transaction t = new Transaction(doc, "Abzugskörper-Familie benennen und platzieren"))
            {
                t.Start();

                // Name setzen
                family.Name = famName;

                // FamilySymbol finden und aktivieren
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Id == family.Id)
                    .ToList();

                if (collector.Count > 0)
                {
                    FamilySymbol symbol = collector.First();
                    if (!symbol.IsActive)
                        symbol.Activate();

                    // WICHTIG: Familie bei (0,0,0) platzieren!
                    // Das Solid in der Familie hat bereits seine Original-Welt-Koordinaten,
                    // daher muss die Familie bei (0,0,0) platziert werden, damit das Solid
                    // an seiner ursprünglichen Position erscheint.
                    XYZ placementPos = XYZ.Zero;

                    // Instanz platzieren
                    placedInstance = doc.Create.NewFamilyInstance(
                        placementPos, 
                        symbol, 
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                }

                t.Commit();
            }

            return placedInstance;
        }
    }

}

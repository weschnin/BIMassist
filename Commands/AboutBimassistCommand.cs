using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using System.Reflection;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class AboutBimassistCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string version = GetInformationalVersion(assembly);
                string buildDate = GetBuildDateText(assembly);

                TaskDialog dialog = new TaskDialog("Info über BIMassist")
                {
                    TitleAutoPrefix = false,
                    MainInstruction = "BIMassist für Autodesk Revit 2026",
                    MainContent =
                        $"Version: {version}\n" +
                        $"Autor: Alexander Weschnin\n" +
                        $"E-Mail: weschnin@gmail.com\n\n" +
                        "BIMassist ist ein Add-In für Autodesk Revit 2026 zur Unterstützung von BIM-, Geometrie-, Material-, Positions-, Leitungs- und Modellierungsabläufen.",
                    ExpandedContent =
                        "Lizenz- und Nutzungsbedingungen\n" +
                        "--------------------------------\n" +
                        "BIMassist ist urheberrechtlich geschützte Software von Alexander Weschnin. " +
                        "Das Add-In darf kostenlos genutzt werden. Eine Weitergabe, Veröffentlichung, Änderung, Dekompilierung oder kommerzielle Verwertung ist nur mit ausdrücklicher Zustimmung des Autors zulässig.\n\n" +
                        "Die Nutzung erfolgt auf eigenes Risiko. Es wird keine Gewährleistung für Fehlerfreiheit, Funktionsumfang, Eignung für einen bestimmten Zweck oder das Erreichen eines bestimmten Erfolgs übernommen. " +
                        "Jede Haftung für direkte oder indirekte Schäden, Datenverluste, Planungsfehler, Modellfehler oder Folgeschäden aus der Nutzung des Add-Ins ist — soweit gesetzlich zulässig — ausgeschlossen.\n\n" +
                        "Autodesk und Revit sind Marken bzw. eingetragene Marken von Autodesk, Inc. BIMassist ist ein unabhängiges Add-In und kein Produkt von Autodesk.\n\n" +
                        $"Build: {buildDate}",
                    FooterText = "© Alexander Weschnin. Alle Rechte vorbehalten."
                };

                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIMassist - Info", "Die Infobox konnte nicht angezeigt werden.\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static string GetInformationalVersion(Assembly assembly)
        {
            string version = assembly
                .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(version))
                return version;

            return assembly.GetName().Version?.ToString() ?? "unbekannt";
        }

        private static string GetBuildDateText(Assembly assembly)
        {
            try
            {
                string location = assembly.Location;
                if (string.IsNullOrWhiteSpace(location))
                    return "unbekannt";

                DateTime lastWrite = System.IO.File.GetLastWriteTime(location);
                return lastWrite.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unbekannt";
            }
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Views;
using System;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RestoreSectionBoxCommand : IExternalCommand
    {
        // identisch zur AWESBox-Logik: Schema-GUID für benannte Snapshots
        private static readonly Guid NamedSchemaGuid = new Guid("A1E7D2C4-9E19-4A1F-B9A0-8D0E0C6C4F11");

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                Schema schema = GetOrCreateNamedSchema();

                var manager = new SnapshotManagerWindow(commandData.Application, doc, schema);
                WpfOwner.ShowModeless(manager, commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static Schema GetOrCreateNamedSchema()
        {
            Schema schema = Schema.Lookup(NamedSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder sb = new SchemaBuilder(NamedSchemaGuid);
            sb.SetSchemaName("AWESBox_SectionBoxSnapshot");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);

            sb.AddSimpleField("Name", typeof(string));
            sb.AddSimpleField("localMin", typeof(string));
            sb.AddSimpleField("localMax", typeof(string));
            sb.AddSimpleField("Origin", typeof(string));
            sb.AddSimpleField("BasisX", typeof(string));
            sb.AddSimpleField("BasisY", typeof(string));
            sb.AddSimpleField("BasisZ", typeof(string));

            sb.AddSimpleField("EyePosition", typeof(string));
            sb.AddSimpleField("ForwardDirection", typeof(string));
            sb.AddSimpleField("UpDirection", typeof(string));

            return sb.Finish();
        }
    }
}
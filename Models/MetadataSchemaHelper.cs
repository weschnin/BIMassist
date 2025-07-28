using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIMassist.Models
{
    // =========================
    // Schema-Erstellung
    // =========================
    public static class MetadataSchemaHelper
    {
        public static readonly Guid SchemaGuid = new Guid("9A1E3E89-8B9A-4BB7-8D29-FA2D9A4B842B");

        public static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("FamilyMetadata");
            builder.AddSimpleField("DeveloperFirstName", typeof(string));
            builder.AddSimpleField("DeveloperLastName", typeof(string));
            builder.AddSimpleField("Owner", typeof(string));
            builder.AddSimpleField("Email", typeof(string));
            builder.AddSimpleField("EditDate", typeof(string));
            builder.AddSimpleField("Description", typeof(string));
            builder.AddSimpleField("PasswordHash", typeof(string));
            return builder.Finish();
        }
    }
}

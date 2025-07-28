using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB;
using System.Security.Cryptography;
using System.Text;

namespace BIMassist.Models
{
    // =========================
    // Daten-Handler
    // =========================
    public static class MetadataStorage
    {
        public static Entity LoadMetadata(Document famDoc, Family family)
        {
            // Sucht den ExtensibleStorage-Eintrag auf family selbst (nicht DataStorage im Projekt!)
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            if (family == null) return null;
            Entity entity = family.GetEntity(schema);
            if (entity == null || !entity.IsValid() || entity.Schema.GUID != schema.GUID)
                return null;
            return entity;
        }

        public static void SaveMetadata(Document famDoc, Family family, Dictionary<string, string> values)
        {
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity entity = new Entity(schema);
            foreach (var kvp in values)
                entity.Set(schema.GetField(kvp.Key), kvp.Value);

            using (Transaction t = new Transaction(famDoc, "Save Family Metadata"))
            {
                t.Start();
                family.SetEntity(entity);
                t.Commit();
            }
        }

        public static string ComputeMD5(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var md5 = MD5.Create())
            {
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = md5.ComputeHash(inputBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public static void CopyMetadata(Document famDoc, Family source, Family target)
        {
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity sourceEntity = source.GetEntity(schema);
            if (sourceEntity == null || !sourceEntity.IsValid()) return;

            Entity targetEntity = new Entity(schema);
            foreach (Field field in schema.ListFields())
                targetEntity.Set(field, sourceEntity.Get<object>(field));

            using (Transaction t = new Transaction(famDoc, "Copy Metadata"))
            {
                t.Start();
                target.SetEntity(targetEntity);
                t.Commit();
            }
        }
    }
}

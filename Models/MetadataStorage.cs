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
            if (family == null) return null;
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity entity = family.GetEntity(schema);
            if (entity == null || !entity.IsValid() || entity.Schema.GUID != schema.GUID)
                return null;
            return entity;
        }

        public static void SaveMetadata(Document famDoc, Family family, Dictionary<string, string> values)
        {
            if (family == null)
                throw new ArgumentNullException(nameof(family), "Family darf nicht null sein!");

            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity entity = new Entity(schema);
            foreach (var kvp in values)
                entity.Set(schema.GetField(kvp.Key), kvp.Value);

            family.SetEntity(entity);
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
            // Source-Family aus famDoc holen (falls nötig)
            Family famSource = new FilteredElementCollector(famDoc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name == source.Name);

            // Target-Family aus famDoc holen (falls nötig)
            Family famTarget = new FilteredElementCollector(famDoc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name == target.Name);

            if (famSource == null || famTarget == null)
                throw new InvalidOperationException("Family-Objekt im Family-Dokument nicht gefunden!");

            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity sourceEntity = famSource.GetEntity(schema);
            if (sourceEntity == null || !sourceEntity.IsValid()) return;

            Entity targetEntity = new Entity(schema);
            foreach (Field field in schema.ListFields())
                targetEntity.Set(field, sourceEntity.Get<object>(field));

            famTarget.SetEntity(targetEntity);
        }
    }
}

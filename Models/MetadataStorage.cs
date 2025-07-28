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
        public static Entity LoadMetadata(Document doc, Family family)
        {
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage));

            foreach (DataStorage ds in collector)
            {
                Entity ent = ds.GetEntity(schema);
                if (ent.IsValid() && ent.Schema.GUID == schema.GUID)
                {
                    if (ent.Get<string>(schema.GetField("Owner")) == family.Name)
                    {
                        return ent;
                    }
                }
            }
            return null;
        }

        public static void SaveMetadata(Document doc, Family family, Dictionary<string, string> values)
        {
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity entity = new Entity(schema);

            foreach (var kvp in values)
            {
                entity.Set(schema.GetField(kvp.Key), kvp.Value);
            }

            using (Transaction t = new Transaction(doc, "Save Family Metadata"))
            {
                t.Start();
                DataStorage ds = DataStorage.Create(doc);
                ds.SetEntity(entity);
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

        public static void CopyMetadata(Document doc, Family source, Family target)
        {
            Schema schema = MetadataSchemaHelper.GetOrCreateSchema();
            Entity sourceEntity = LoadMetadata(doc, source);

            if (sourceEntity == null)
                return;

            string sourceHash = sourceEntity.Get<string>(schema.GetField("PasswordHash"));
            string inputPassword = Microsoft.VisualBasic.Interaction.InputBox("Passwort für Quellfamilie eingeben:", "Kopieren bestätigen");
            if (sourceHash != ComputeMD5(inputPassword))
                return;

            Entity targetEntity = new Entity(schema);
            foreach (Field field in schema.ListFields())
            {
                targetEntity.Set(field, sourceEntity.Get<object>(field));
            }

            using (Transaction t = new Transaction(doc, "Copy Metadata"))
            {
                t.Start();
                DataStorage ds = DataStorage.Create(doc);
                ds.SetEntity(targetEntity);
                t.Commit();
            }
        }
    }
}

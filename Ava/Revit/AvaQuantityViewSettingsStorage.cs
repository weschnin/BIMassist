using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>One project-owned presentation state, independent of takeoff run history.</summary>
public static class AvaQuantityViewSettingsStorage
{
    internal static readonly Guid SchemaGuid = new("dcd81d44-26cc-4abd-a9da-94f234406470");
    private const int Version = 1;
    private const string SchemaName = "BIMassistAvaQuantityViewSettings";

    public static AvaQuantityViewSettings? Load(Document document)
    {
        ValidateDocument(document);
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return null;
        ValidateSchema(schema);
        var stores = FindStores(document);
        if (stores.Count > 1)
            throw new InvalidDataException("Das Projekt enthält mehrere Mengenermittlungs-Ansichtseinstellungen; der Speicher ist nicht eindeutig.");
        if (stores.Count == 0) return null;
        var entity = stores[0].GetEntity(schema);
        if (!entity.IsValid() || entity.Get<int>("SchemaVersion") != Version)
            throw new InvalidDataException("Die gespeicherten Ansichtseinstellungen haben eine ungültige oder nicht unterstützte Version.");
        try
        {
            var settings = AvaQuantityViewSettingsCodec.Deserialize(entity.Get<string>("Json"));
            if (settings.SchemaVersion != Version)
                throw new InvalidDataException("Die JSON-Version der Ansichtseinstellungen wird nicht unterstützt.");
            return settings;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            throw new InvalidDataException("Die gespeicherten Ansichtseinstellungen sind ungültig.", ex);
        }
    }

    /// <summary>The caller owns an explicit transaction, including rollback on failure.</summary>
    public static void Save(Document document, AvaQuantityViewSettings settings)
    {
        ValidateDocument(document);
        ArgumentNullException.ThrowIfNull(settings);
        if (!document.IsModifiable)
            throw new InvalidOperationException("Ansichtseinstellungen dürfen nur in einer ausdrücklich gestarteten Revit-Transaktion gespeichert werden.");
        var json = AvaQuantityViewSettingsCodec.Serialize(settings);
        if (settings.SchemaVersion != Version) throw new InvalidDataException("Die Version der Ansichtseinstellungen wird nicht unterstützt.");
        // Validate existing data before changing anything; never overwrite a corrupt/ambiguous store.
        var previous = Load(document);
        if (previous != null && string.Equals(AvaQuantityViewSettingsCodec.Serialize(previous), json, StringComparison.Ordinal)) return;
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null)
        {
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("SchemaVersion", typeof(int));
            builder.AddSimpleField("Json", typeof(string));
            schema = builder.Finish();
        }
        var store = FindStores(document).SingleOrDefault() ?? DataStorage.Create(document);
        var entity = new Entity(schema);
        entity.Set("SchemaVersion", Version);
        entity.Set("Json", json);
        store.SetEntity(entity);
        var readback = Load(document);
        if (readback == null || !string.Equals(AvaQuantityViewSettingsCodec.Serialize(readback), json, StringComparison.Ordinal))
            throw new InvalidDataException("Die Rückleseprüfung der Ansichtseinstellungen ist fehlgeschlagen; die Transaktion muss zurückgenommen werden.");
    }

    private static void ValidateDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsValidObject) throw new InvalidOperationException("Das gebundene Revit-Projekt ist geschlossen oder ungültig.");
        if (document.IsFamilyDocument) throw new InvalidOperationException("Ansichtseinstellungen benötigen ein Revit-Projekt, keine Familie.");
    }

    private static void ValidateSchema(Schema schema)
    {
        if (schema.SchemaName != SchemaName || schema.ListFields().Count != 2
            || !IsField(schema.GetField("SchemaVersion"), typeof(int))
            || !IsField(schema.GetField("Json"), typeof(string)))
            throw new InvalidDataException("Das Revit-Schema der Ansichtseinstellungen ist inkompatibel.");
    }

    private static bool IsField(Field? field, Type type) =>
        field != null && field.ContainerType == ContainerType.Simple && field.ValueType == type;

    private static System.Collections.Generic.List<DataStorage> FindStores(Document document)
    {
        using var collector = new FilteredElementCollector(document);
        return collector.OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .Where(store => store.GetEntitySchemaGuids().Contains(SchemaGuid)).ToList();
    }
}

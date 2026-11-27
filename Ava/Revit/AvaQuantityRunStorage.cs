using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Persists exactly one versioned AVA takeoff snapshot inside the active Revit project.</summary>
public static class AvaQuantityRunStorage
{
    public static AvaQuantityRun? Load(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var schema = AvaQuantityRunStorageSchema.GetRunSchema();
        var stores = FindStores(document, schema);
        if (stores.Count == 0) return null;
        if (stores.Count != 1)
            throw new InvalidDataException("Das Revit-Projekt enthält mehrere BIMassist-Mengenermittlungsspeicher; der letzte Lauf wurde nicht eindeutig geladen.");

        var entity = stores[0].GetEntity(schema);
        int expectedCount = entity.Get<int>("ChunkCount");
        var chunkEntities = entity.Get<IList<Entity>>("PayloadChunks");
        if (expectedCount <= 0 || chunkEntities == null || chunkEntities.Count != expectedCount)
            throw new InvalidDataException("Der im Revit-Projekt gespeicherte Mengenermittlungslauf ist unvollständig.");
        var chunks = chunkEntities
            .Select(chunk => (Index: chunk.Get<int>("Index"), Payload: chunk.Get<string>("Payload")))
            .OrderBy(chunk => chunk.Index)
            .ToList();
        if (chunks.Where((chunk, index) => chunk.Index != index).Any())
            throw new InvalidDataException("Die Datenblöcke des gespeicherten Mengenermittlungslaufs sind doppelt oder fehlen.");

        var run = AvaQuantityRunCodec.FromStorageChunks(chunks.Select(chunk => chunk.Payload).ToList());
        var storedRunId = entity.Get<string>("RunId");
        var storedCreatedUtc = entity.Get<string>("CreatedUtc");
        if (!string.Equals(storedRunId, run.RunId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(storedCreatedUtc, run.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidDataException("Die Metadaten des gespeicherten Mengenermittlungslaufs stimmen nicht mit dem Dateninhalt überein.");
        return run;
    }

    /// <summary>Must be called inside the caller's explicit Revit Transaction.</summary>
    public static void Save(Document document, AvaQuantityRun run)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(run);
        if (!document.IsModifiable)
            throw new InvalidOperationException("Der Mengenermittlungslauf darf nur in einer ausdrücklich gestarteten Revit-Transaktion gespeichert werden.");

        var schema = AvaQuantityRunStorageSchema.GetRunSchema();
        var chunkSchema = AvaQuantityRunStorageSchema.GetChunkSchema();
        var stores = FindStores(document, schema);
        if (stores.Count > 1)
            throw new InvalidDataException("Das Revit-Projekt enthält mehrere BIMassist-Mengenermittlungsspeicher; der vorhandene Lauf bleibt unverändert.");

        var payloadChunks = AvaQuantityRunCodec.ToStorageChunks(run);
        var chunkEntities = new List<Entity>(payloadChunks.Count);
        for (int index = 0; index < payloadChunks.Count; index++)
        {
            var chunk = new Entity(chunkSchema);
            chunk.Set("Index", index);
            chunk.Set("Payload", payloadChunks[index]);
            chunkEntities.Add(chunk);
        }

        var entity = new Entity(schema);
        entity.Set("SchemaVersion", AvaQuantityRunCodec.CurrentSchemaVersion);
        entity.Set("RunId", run.RunId.ToString("N"));
        entity.Set("CreatedUtc", run.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        entity.Set("ChunkCount", chunkEntities.Count);
        entity.Set<IList<Entity>>("PayloadChunks", chunkEntities);

        var store = stores.SingleOrDefault() ?? DataStorage.Create(document);
        store.SetEntity(entity);
    }

    private static List<DataStorage> FindStores(Document document, Schema schema) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(DataStorage))
            .Cast<DataStorage>()
            .Where(store => store.GetEntity(schema).IsValid())
            .ToList();
}

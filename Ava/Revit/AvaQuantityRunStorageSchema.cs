using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIMassist.Ava.Revit;

internal static class AvaQuantityRunStorageSchema
{
    private static readonly Guid RunSchemaGuid = new("3ceb61cb-3df4-4fde-bbc7-0277d9390d6e");
    private static readonly Guid ChunkSchemaGuid = new("ce1b75fc-6b4a-41be-927b-df1362f47f50");

    public static Schema GetRunSchema()
    {
        var existing = Schema.Lookup(RunSchemaGuid);
        if (existing != null) return existing;
        var chunkSchema = GetChunkSchema();
        var builder = new SchemaBuilder(RunSchemaGuid);
        builder.SetSchemaName("BIMassist_AVA_QuantityRun_v1");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetVendorId("github.com.weschnin");
        builder.AddSimpleField("SchemaVersion", typeof(int));
        builder.AddSimpleField("RunId", typeof(string));
        builder.AddSimpleField("CreatedUtc", typeof(string));
        builder.AddSimpleField("ChunkCount", typeof(int));
        var chunks = builder.AddArrayField("PayloadChunks", typeof(Entity));
        chunks.SetSubSchemaGUID(chunkSchema.GUID);
        return builder.Finish();
    }

    internal static Schema GetChunkSchema()
    {
        var existing = Schema.Lookup(ChunkSchemaGuid);
        if (existing != null) return existing;
        var builder = new SchemaBuilder(ChunkSchemaGuid);
        builder.SetSchemaName("BIMassist_AVA_QuantityRunChunk_v1");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetVendorId("github.com.weschnin");
        builder.AddSimpleField("Index", typeof(int));
        builder.AddSimpleField("Payload", typeof(string));
        return builder.Finish();
    }
}

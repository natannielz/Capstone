using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

/// <summary>Private, bounded generated documents committed with their source owner's receipt.
/// Uploads remain in the private evidence store. This store never accepts user-supplied HTML,
/// grants access, mutates issued content, or treats its hash as an authorization credential.</summary>
internal sealed class IssuedDocumentStore(IMongoDatabase database)
{
    internal const string Collection = "issuedDocuments", Kind = "fulfillment.surat-jalan", Operation = "fulfillment.surat-jalan.issue";
    internal const int MaximumBytes = 512 * 1024;
    private IMongoCollection<BsonDocument> Rows => database.GetCollection<BsonDocument>(Collection);
    internal Task StorageAsync(CancellationToken ct) => InventoryRepository.RequireStorageAsync(database, Collection,
        IssuedDocumentSchemas.Validator(), IssuedDocumentSchemas.Key(), ct);
    internal static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    internal static BsonDocument Result(BsonDocument row) => new() { { "documentId", row["_id"] }, { "shipmentId", row["sourceId"] },
        { "shipmentRevision", row["sourceRevision"] }, { "version", row["version"] }, { "contentHash", row["contentHash"] },
        { "byteLength", row["byteLength"] }, { "issuedAt", row["issuedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Kind + ".issued" },
        { "operation", Operation }, { "actorId", row["issuedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", CatalogRepository.Digest(row) }, { "reason", row["reason"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["issuedAt"] }, { "result", Result(row) } };
    internal async Task InsertAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        RequireTransaction(tx);
        if (Encoding.UTF8.GetByteCount(row["content"].AsString) is < 1 or > MaximumBytes) throw Invalid();
        if (await Rows.Find(tx, new BsonDocument { { "kind", Kind }, { "sourceId", row["sourceId"] }, { "sourceRevision", row["sourceRevision"] } }).AnyAsync(ct))
            throw new Mongo.PersistenceConflictException("shipment_document_already_issued", "Read the existing issued document");
        await Rows.InsertOneAsync(tx, row, cancellationToken: ct);
    }
    internal async Task<BsonDocument?> FindAsync(IClientSessionHandle tx, long sourceId, CancellationToken ct) {
        RequireTransaction(tx);
        var row = await Rows.Find(tx, new BsonDocument { { "kind", Kind }, { "sourceId", sourceId }, { "sourceRevision", 1L } }).SingleOrDefaultAsync(ct);
        if (row is not null) return row;
        // A missing root is not permission to issue a replacement under a new request key.
        // Reverse accepted effects distinguish never issued from damaged historical storage.
        foreach (var (collection, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "operation", Operation }, { "result.shipmentId", sourceId } }),
            ("auditEvents", new() { { "operation", Operation }, { "result.shipmentId", sourceId } }),
            ("outboxEvents", new() { { "kind", Kind + ".issued" }, { "payload.shipmentId", sourceId } })
        }) if (await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Limit(1).AnyAsync(ct)) throw Invalid();
        return null;
    }
    internal async Task<BsonDocument> VerifyAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument expectedSnapshot, CancellationToken ct) {
        RequireTransaction(tx);
        try {
            var source = row["snapshot"].AsBsonDocument; var content = row["content"].AsString;
            if (row["kind"] != Kind || row["sourceId"] != source["shipmentId"] || row["sourceRevision"] != source["shipmentRevision"] ||
                row["version"] != 1L || row["renderer"] != "surat-jalan-html-v1" || row["mediaType"] != "text/html; charset=utf-8" ||
                !CatalogSchemas.SemanticEquals(source, expectedSnapshot) || row["contentHash"] != Hash(content) ||
                !row["byteLength"].IsInt64 || row["byteLength"].AsInt64 != Encoding.UTF8.GetByteCount(content) || row["byteLength"].AsInt64 is < 1 or > MaximumBytes)
                throw Invalid();
            var audit = await database.GetCollection<BsonDocument>("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["issuedBy"] },
                { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            if (audit is null || !CatalogSchemas.SemanticEquals(audit, Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), Result(row))) throw Invalid();
            return row;
        } catch (Exception failure) when (failure is InvalidCastException or KeyNotFoundException or FormatException or OverflowException) { throw Invalid(); }
    }
    private void RequireTransaction(IClientSessionHandle tx) { if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Invalid(); }
    internal static AuthorizationDeniedException Invalid() => new("shipment_document_unavailable");
}

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
internal sealed class IssuedDocumentStore(IMongoDatabase database, bool receiptProof = false)
{
    internal const string Collection = "issuedDocuments", Kind = "fulfillment.surat-jalan", Operation = "fulfillment.surat-jalan.issue";
    internal const string ProofCollection = "issuedReceiptProofs", ProofKind = "fulfillment.receipt-proof", ProofOperation = "fulfillment.receipt-proof.issue";
    internal const int MaximumBytes = 512 * 1024;
    internal const int MaximumProofBytes = 2 * 1024 * 1024;
    private int ContentLimit => receiptProof ? MaximumProofBytes : MaximumBytes;
    private string DocumentKind => receiptProof ? ProofKind : Kind;
    private string DocumentOperation => receiptProof ? ProofOperation : Operation;
    private string Renderer => receiptProof ? "receipt-proof-html-v1" : "surat-jalan-html-v1";
    private IMongoCollection<BsonDocument> Rows => database.GetCollection<BsonDocument>(receiptProof ? ProofCollection : Collection);
    internal Task StorageAsync(CancellationToken ct) => InventoryRepository.RequireStorageAsync(database, receiptProof ? ProofCollection : Collection,
        receiptProof ? IssuedReceiptProofSchemas.Validator() : IssuedDocumentSchemas.Validator(), IssuedDocumentSchemas.Key(), ct);
    internal static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    internal static BsonDocument Result(BsonDocument row) => new() { { "documentId", row["_id"] }, { "shipmentId", row["sourceId"] },
        { "shipmentRevision", row["sourceRevision"] }, { "version", row["version"] }, { "contentHash", row["contentHash"] },
        { "byteLength", row["byteLength"] }, { "issuedAt", row["issuedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Kind + ".issued" },
        { "operation", Operation }, { "actorId", row["issuedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", CatalogRepository.Digest(row) }, { "reason", row["reason"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["issuedAt"] }, { "result", Result(row) } };
    internal BsonDocument CreateAudit(BsonDocument row) {
        var audit = Audit(row); audit["kind"] = DocumentKind + ".issued"; audit["operation"] = DocumentOperation; return audit;
    }
    internal async Task InsertAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        RequireTransaction(tx);
        var length = Encoding.UTF8.GetByteCount(row["content"].AsString);
        if (length < 1 || length > ContentLimit) throw Invalid();
        if (row["kind"] != DocumentKind) throw Invalid();
        if (await Rows.Find(tx, new BsonDocument { { "kind", DocumentKind }, { "sourceId", row["sourceId"] }, { "sourceRevision", row["sourceRevision"] } }).AnyAsync(ct))
            throw new Mongo.PersistenceConflictException("shipment_document_already_issued", "Read the existing issued document");
        await Rows.InsertOneAsync(tx, row, cancellationToken: ct);
    }
    internal async Task<BsonDocument?> FindAsync(IClientSessionHandle tx, long sourceId, CancellationToken ct) {
        RequireTransaction(tx);
        var row = await Rows.Find(tx, new BsonDocument { { "kind", DocumentKind }, { "sourceId", sourceId }, { "sourceRevision", 1L } }).SingleOrDefaultAsync(ct);
        if (row is not null) return row;
        // A missing root is not permission to issue a replacement under a new request key.
        // Reverse accepted effects distinguish never issued from damaged historical storage.
        foreach (var (collection, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "operation", DocumentOperation }, { "result.shipmentId", sourceId } }),
            ("auditEvents", new() { { "operation", DocumentOperation }, { "result.shipmentId", sourceId } }),
            ("outboxEvents", new() { { "kind", DocumentKind + ".issued" }, { "payload.shipmentId", sourceId } })
        }) if (await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Limit(1).AnyAsync(ct)) throw Invalid();
        return null;
    }
    internal async Task<BsonDocument> VerifyAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument expectedSnapshot, CancellationToken ct) {
        RequireTransaction(tx);
        try {
            var source = row["snapshot"].AsBsonDocument; var content = row["content"].AsString;
            if (row["kind"] != DocumentKind || row["sourceId"] != source["shipmentId"] || row["sourceRevision"] != source["shipmentRevision"] ||
                row["version"] != 1L || row["renderer"] != Renderer || row["mediaType"] != "text/html; charset=utf-8" ||
                !CatalogSchemas.SemanticEquals(source, expectedSnapshot) || row["contentHash"] != Hash(content) ||
                !row["byteLength"].IsInt64 || row["byteLength"].AsInt64 != Encoding.UTF8.GetByteCount(content) || row["byteLength"].AsInt64 < 1 || row["byteLength"].AsInt64 > ContentLimit)
                throw Invalid();
            var audit = await database.GetCollection<BsonDocument>("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["issuedBy"] },
                { "operation", DocumentOperation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            if (audit is null || !CatalogSchemas.SemanticEquals(audit, CreateAudit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), Result(row))) throw Invalid();
            return row;
        } catch (Exception failure) when (failure is InvalidCastException or KeyNotFoundException or FormatException or OverflowException) { throw Invalid(); }
    }
    private void RequireTransaction(IClientSessionHandle tx) { if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Invalid(); }
    internal static AuthorizationDeniedException Invalid() => new("shipment_document_unavailable");
}

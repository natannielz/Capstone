using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

/// <summary>Independent, finite processor approval; no runtime-created approval or user-provided job authority.</summary>
internal sealed class PdfEvidenceValidationService(IMongoDatabase database, IClock clock, string consumer, string pipeline,
    string store, string hostAcceptanceReference, string clamdConfigurationAcceptanceReference)
{
    internal async Task RequireStorageAsync(CancellationToken ct)
    {
        foreach (var name in PdfEvidenceValidationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, PdfEvidenceValidationSchemas.Validator(name), PdfEvidenceValidationSchemas.Key(name), ct);
        foreach (var name in new[] { "evidenceLifecycles", "evidenceLifecycleAudits" })
            await InventoryRepository.RequireStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
    }
    internal BsonDocument PolicyFilter() => new("$and", new BsonArray {
        PdfEvidenceValidationSchemas.Validator(PdfEvidenceValidationRecords.Policies), new BsonDocument {
            { "consumerId", consumer }, { "pipelineId", pipeline }, { "storeId", store }, { "active", true },
            { "hostAcceptanceRef", hostAcceptanceReference }, { "clamdConfigurationAcceptanceRef", clamdConfigurationAcceptanceReference },
            { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) },
            { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) }, { "fence", new BsonDocument("$lt", long.MaxValue) } } });
    internal async Task<bool> IsApprovedAsync(CancellationToken ct) =>
        await database.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Policies).Find(PolicyFilter()).AnyAsync(ct);
    internal async Task<BsonDocument> TouchPolicyAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Unavailable();
        return await database.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Policies).FindOneAndUpdateAsync(tx,
            PolicyFilter(), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    }
    internal void RequireResultPolicy(BsonDocument result, BsonDocument policy)
    {
        if (result["consumerId"] != consumer || result["pipelineId"] != pipeline || result["storeId"] != store ||
            result["policyId"] != policy["_id"] || result["policyRevision"] != policy["revision"] ||
            result["checkedAt"].CompareTo(policy["approvedAt"]) < 0 || result["checkedAt"].CompareTo(policy["expiresAt"]) >= 0)
            throw Unavailable();
    }
    internal static BsonDocument PolicyIdentity(BsonDocument policy) { var copy = policy.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static PersistenceConflictException Unavailable() => new("evidence_pdf_validation_policy_unavailable", "PDF processor approval is not current");
}

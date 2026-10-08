using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public async Task<CatalogIdentifierResult?> ReadIdentifierReceiptAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, CancellationToken ct = default)
    {
        ValidateRead(audit, true);
        if (!CatalogIdentifierEvidence.Operations.Contains(operation) || !CatalogReceiptRules.Key(key)) throw CatalogIdentifierPolicy.Invalid();
        await CatalogIdentifierRecords.RequireStorageAsync(database, ct); var auditId = await ids.NextAsync("auditEvents", ct);
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, tx, token);
            var receipt = await repository.Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            var result = receipt is null ? null : await IdentifierReceiptAsync(context, receipt, operation, key, token);
            await AuditReadAsync(context, auditId.Value, "catalog.identifier.operation", audit,
                new BsonDocument { { "operation", operation }, { "key", key } }, result is null ? 0 : 1, token);
            return result;
        }, ct);
    }
    private async Task<CatalogIdentifierResult> IdentifierReceiptAsync(Context context, BsonDocument receipt, string operation, string key, CancellationToken ct)
    {
        if (!receipt.TryGetValue("catalogIdentifier", out var reference) || !reference.IsBsonDocument ||
            !CatalogRepository.Positive(reference.AsBsonDocument, "auditId")) throw CatalogIdentifierRecords.Broken();
        var audit = await repository.Rows("auditEvents").Find(context.Tx, F.Eq("_id", reference["auditId"])).SingleOrDefaultAsync(ct) ?? throw CatalogIdentifierRecords.Broken();
        CatalogIdentifierEvidence.RequireReceipt(receipt, audit);
        if (receipt["actorId"].AsInt64 != context.Actor.UserId || receipt["operation"] != operation || receipt["key"] != key) throw CatalogIdentifierRecords.Broken();
        var owner = CatalogIdentifierEvidence.RequireAudit(audit); RequireOwner(context, owner);
        var result = CatalogIdentifierEvidence.Result(receipt["result"].AsBsonDocument);
        var sku = await MasterAsync(context, "skus", result.SkuId.Value, ct);
        if (RowOwner(sku) != owner) throw CatalogIdentifierRecords.Broken();
        // These are original immutable results; later code corrections or retirement do not
        // silently replace their identifiers or revisions with current values.
        await CatalogIdentifierRecords.FenceAsync(repository.Rows("auditEvents"), context.Tx, audit, ct);
        await CatalogIdentifierRecords.FenceAsync(repository.Rows("idempotencyRecords"), context.Tx, receipt, ct);
        return result;
    }
}

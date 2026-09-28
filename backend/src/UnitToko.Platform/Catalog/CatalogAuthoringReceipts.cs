using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public async Task<CatalogMutationResult?> ReadOperationReceiptAsync(ActorContext actor, string operation, string key, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit, true);
        if (!CatalogReceiptRules.Operations.Contains(operation) || !CatalogReceiptRules.Key(key)) throw BadRequest();
        var auditId = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct);
            var receipt = await repository.Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).FirstOrDefaultAsync(ct);
            CatalogMutationResult? result = receipt is null ? null : await ReceiptAsync(context, receipt, operation, key, ct);
            await AuditReadAsync(context, auditId.Value, "catalog.operation", audit,
                new BsonDocument { { "operation", operation }, { "key", key } }, result is null ? 0 : 1, ct);
            return result;
        }, cancellationToken);
    }
    private async Task<CatalogMutationResult> ReceiptAsync(Context context, BsonDocument receipt, string operation, string key, CancellationToken ct)
    {
        CatalogReceiptRules.RequireReceipt(receipt, context.Actor.UserId, operation, key);
        var audit = await repository.Rows("auditEvents").Find(context.Tx,
            F.Eq("_id", receipt["catalogAuthoring"]["auditId"])).FirstOrDefaultAsync(ct);
        if (audit is null) throw CatalogReceiptRules.Invalid();
        var owners = CatalogReceiptRules.RequireAudit(audit);
        if (audit["actorId"] != receipt["actorId"] || audit["operation"] != receipt["operation"] || audit["bodyHash"] != receipt["bodyHash"] ||
            audit["createdAt"] != receipt["createdAt"] || audit["result"] != receipt["result"]) throw CatalogReceiptRules.Invalid();
        foreach (var owner in owners) RequireOwner(context, owner);
        return CatalogReceiptRules.Result(receipt["result"].AsBsonDocument);
    }
}

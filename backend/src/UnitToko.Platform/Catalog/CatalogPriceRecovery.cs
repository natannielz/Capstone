using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogPriceService
{
    private sealed record RecoveryRecord(object Body, AuthorizationCommandMetadata Metadata, long ProposalId);

    /// <summary>Reference-only recovery. It can return a prior receipt, never manufacture a
    /// replacement proposal or decision. Current action/person/session authority is checked again.</summary>
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, long skuId, string action, string key, CancellationToken ct = default)
    {
        if (action is not ("propose" or "approve" or "reject" or "withdraw") || !CatalogReceiptRules.Key(key))
            throw B.Invalid("invalid_catalog_price_recovery");
        var access = action is "propose" or "withdraw" ? CatalogPriceAccess.Prepare : CatalogPriceAccess.Review;
        var permission = Permission(access); var operation = B.Prefix + action;
        var recovered = await ReadAsync<RecoveryRecord?>(actor, skuId, access, B.Prefix + "recovery",
            new() { { "skuId", skuId }, { "action", action }, { "keyDigest", CatalogRepository.Digest(new BsonString(key)) } },
            async (context, token) => {
                var receipt = await Rows("idempotencyRecords").Find(context.Tx, F.Eq("actorId", actor.UserId) &
                    F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
                if (receipt is null) return null;
                BsonDocument result = null!;
                B.Guard(() => {
                    result = receipt["result"].AsBsonDocument;
                    if (!B.Positive(result, "proposalId") || !B.Positive(result, "skuId") || result["skuId"] != skuId) throw B.Invalid();
                });
                var proposal = await ProposalAsync(context, result["proposalId"].AsInt64, token);
                var row = proposal;
                object body;
                if (action == "propose") body = new {
                    skuId, expected = B.Expected(proposal["expected"].AsBsonDocument), input = B.Input(proposal["input"].AsBsonDocument) };
                else {
                    if (!B.Positive(result, "decisionId")) throw B.Invalid();
                    row = await GetAsync(context.Tx, B.Decisions, result["decisionId"].AsInt64, token);
                    B.Decision(row, proposal); await ProvenanceAsync(context.Tx, row, proposal, token);
                    body = new { proposalId = proposal["_id"].AsInt64, expectedRevision = row["revision"].AsInt64,
                        decision = B.DecisionKind(row["decision"].AsString), reviewReference = row["reviewReference"].AsString };
                }
                if (B.Actor(row).AsInt64 != actor.UserId || B.Operation(row) != operation || row["requestKey"] != key ||
                    !B.Result(row).Equals(result)) throw B.Invalid();
                return new(body, new(key, row["reason"].AsString, row["correlationId"].AsString), proposal["_id"].AsInt64);
            }, ct);
        if (recovered is null) return null;

        // The first transaction discovers only immutable request facts. The command transaction
        // reloads them and the receipt, fences current authority and refuses any new work.
        return await CommandAsync(actor, operation, permission, recovered.Metadata, recovered.Body,
            async (snapshot, tx, token) => {
                var context = await ContextAsync(actor, snapshot, tx, skuId, permission, token);
                var proposal = await ProposalAsync(context, recovered.ProposalId, token);
                return (context, access == CatalogPriceAccess.Review ?
                    new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64) : null);
            }, (_, _) => throw new InvalidOperationException("Receipt-only recovery cannot execute new price work"),
            ct, requireReceipt: true);
    }
}

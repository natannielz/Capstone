using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

public sealed partial class BankReceiptService
{
    internal async Task<(IReadOnlyList<BsonDocument> Items, long? NextAfterId)> PaymentCandidatesAsync(ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument party, long? afterId, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        var context = new Context(actor, authority, tx, false);
        var selected = await ReceiptIdsAsync(context, afterId, ct); var result = new List<BsonDocument>();
        foreach (var item in selected.Take(25)) {
            if (new[] { "fulfillmentOrganizationId", "shopId", "channelId" }.Any(field => item[field] != party[field]) || !item["warehouseId"].IsBsonNull) continue;
            var bank = await PaymentSourceAsync(actor, authority, tx, item["_id"].AsInt64, ct);
            try {
                _ = await new PaymentReceiptPurposeRecords(database).CustomerAsync(tx, bank.Receipt["_id"].AsInt64,
                    bank.Receipt["snapshotHash"], party, false, ct);
            } catch (PersistenceConflictException error) when (error.Code is "bank_receipt_purpose_conflict" or "payment_receipt_payer_conflict") {
                continue;
            }
            result.Add(bank.Receipt);
        }
        await FenceAsync(context, ct);
        // The cursor follows scanned receipts, even when every item belongs to
        // another purpose. The picker cannot loop or lose later eligible receipts.
        return (result, selected.Count > 25 ? selected[24]["_id"].AsInt64 : null);
    }
}

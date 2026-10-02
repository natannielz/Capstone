using MongoDB.Bson;
using MongoDB.Driver;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentAllocationService
{
    private async Task RequireNoOrphanSettlementJournalAsync(Context context, BsonDocument party, CancellationToken ct) {
        // This reverse scan also applies to invoice-oriented reads, although settlement
        // advances receipt heads only and must never appear in an invoice event chain.
        var filter = new BsonDocument { { "source.namespace", D.Operation }, { "dimensions.organizationId", party["fulfillmentOrganizationId"] },
            { "dimensions.shopId", party["shopId"] }, { "dimensions.channelId", party["channelId"] }, { "dimensions.buyingOrganizationId", party["buyingOrganizationId"] } };
        using var cursor = await records.Rows("journalEntries").Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var journal in cursor.Current) {
            if (journal["source"]["eventKind"] != "refund.bank-debit-settled") throw B.Broken();
            var row = await records.RawSettlementAsync(context.Tx, journal["source"]["documentId"].AsInt64, ct);
            R.Equal(row["journalId"], journal["_id"]);
        }
    }
}

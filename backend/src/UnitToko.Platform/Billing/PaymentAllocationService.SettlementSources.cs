using MongoDB.Bson;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentAllocationService
{
    private async Task ValidateSettlementSourceAsync(Context context, BsonDocument row, CancellationToken ct) {
        var observed = await records.RawSettlementAsync(context.Tx, row["_id"].AsInt64, ct); R.Equal(observed, row);
        var source = await entitlements.ApprovalSourceAsync(context.Actor, context.Authority, context.Tx,
            row["caseId"].AsInt64, row["returnId"].AsInt64, "refund", false, ct, deferReadCaptureFences: true);
        R.Equal(row["invoice"], source.Invoice.MoneySource);
        R.Equal(row["invoiceId"], source.Request["invoiceId"]); R.Equal(row["party"], PaymentDraftBson.Party(new BsonArray { source.Invoice.MoneySource }));
        var bank = await verifications.SettlementBankOwner.SettlementHistorySourceAsync(context.Actor, context.Authority, context.Tx, observed, ct);
        R.Equal(row["debit"], bank.Debit); R.Equal(row["cashAccount"], bank.CashAccount);
        var frozen = B.Snapshot(observed);
        void Current() { source.RequireContext(context.Actor, context.Authority, context.Tx); source.RequireCurrent(); bank.RequireCurrent(); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) {
            Current(); await source.FenceReadAsync(token); await bank.FenceAsync(token);
            var actual = await records.RawSettlementAsync(context.Tx, row["_id"].AsInt64, token); R.Equal(B.Snapshot(actual), frozen);
            await records.TouchAsync(context.Tx, D.Events, actual, token); Current(); token.ThrowIfCancellationRequested();
        }
        context.Invoices.Add(source.Invoice.MoneySource); context.Current.Add(Current); context.SourceFences.Add(Fence);
        Current();
    }
}

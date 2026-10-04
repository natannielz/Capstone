using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

public sealed record PaymentAllocationInvoiceChoice(long InvoiceId, string Number, long Version, Money Total,
    Money Paid, Money Credited, Money Outstanding, string BuyerName, string SellerName, string DueDate);
public sealed record PaymentAllocationInvoiceChoicePage(ImmutableArray<PaymentAllocationInvoiceChoice> Items, long? NextBeforeInvoiceId);

public sealed partial class PaymentAllocationService
{
    private const int InvoiceChoicePageSize = 10;

    // The accepted verification, not an optional client party filter, selects this desk's invoices.
    public async Task<PaymentAllocationInvoiceChoicePage> InvoiceChoicesAsync(ActorContext actor, long verificationId,
        long? beforeInvoiceId = null, int limit = InvoiceChoicePageSize, CancellationToken ct = default) {
        if (verificationId <= 0 || beforeInvoiceId is <= 0 || limit is < 1 or > InvoiceChoicePageSize) throw PaymentAllocationPolicy.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync<PaymentAllocationInvoiceChoicePage>(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, true);
            await SourceAsync(context, verificationId, true, token);
            var party = context.Source.Decision["source"]["party"].AsBsonDocument;
            var receipt = context.Source.Bank.Receipt;
            await PolicyAsync(context, token);
            _ = await records.BalanceAsync(tx, true, receipt["_id"].AsInt64, party, receipt["amount"], receipt["snapshotHash"],
                row => ValidateSourceAsync(context, row, token), token);

            // Match every constituent invoice scope against every required permission and
            // the four canonical party IDs before sorting or limiting candidate IDs.
            var serializer = BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>();
            var scoped = new BsonArray(Require(context).Select(query => (BsonValue)ScopeQuery.Create(query)
                .Render(new RenderArgs<BsonDocument>(serializer, BsonSerializer.SerializerRegistry))));
            scoped.Add(party.DeepClone());
            var pipeline = new List<BsonDocument>();
            if (beforeInvoiceId is { } before) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$lt", before))));
            pipeline.Add(new("$set", new BsonDocument("scopeCount", new BsonDocument("$size", "$scopes"))));
            pipeline.Add(new("$unwind", "$scopes"));
            pipeline.Add(new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray {
                "$scopes", new BsonDocument { { "invoiceId", "$_id" }, { "scopeCount", "$scopeCount" } } }))));
            pipeline.Add(new("$match", new BsonDocument("$and", scoped)));
            pipeline.Add(new("$group", new BsonDocument { { "_id", "$invoiceId" }, { "matches", new BsonDocument("$sum", 1) },
                { "expected", new BsonDocument("$first", "$scopeCount") } }));
            pipeline.Add(new("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$matches", "$expected" }))));
            pipeline.Add(new("$sort", new BsonDocument("_id", -1)));
            pipeline.Add(new("$limit", limit + 1));
            var selected = await records.Rows(InvoiceBson.Invoices).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token);

            var choices = ImmutableArray.CreateBuilder<PaymentAllocationInvoiceChoice>();
            foreach (var candidate in selected.Take(limit)) {
                var id = candidate["_id"].AsInt64;
                var invoice = await invoices.AllocationSourceAsync(actor, authority, tx, id, token);
                if (!PaymentDraftBson.Party(new BsonArray { invoice }).Equals(party)) throw PaymentAllocationBson.Broken();
                context.Invoices.Add(invoice); _ = Require(context);
                var funds = await records.BalanceAsync(tx, false, id, party, invoice["total"], invoice["snapshotHash"],
                    row => ValidateSourceAsync(context, row, token), token);
                var total = InvoiceBson.Money(invoice["total"]);
                var paid = InvoiceBson.Money(funds.Balance["paid"]);
                var credited = InvoiceBson.Money(funds.Balance["credited"]);
                var outstanding = new Money(total.Value - paid.Value - credited.Value);
                if (outstanding.Value == 0) continue;
                choices.Add(new(id, FormattableString.Invariant($"{invoice["numberEntity"].AsInt64}/{invoice["numberChannel"].AsInt64}/{invoice["numberYear"].AsInt64}/{invoice["number"].AsInt64}"),
                    funds.Balance["version"].AsInt64, total, paid, credited, outstanding, invoice["buyerName"].AsString,
                    invoice["sellerName"].AsString, invoice["dueDate"].AsString));
            }
            await RequireNoOrphanJournalAsync(context, party, token);
            await AuditReadAsync(context, "invoice_choices", verificationId, token);
            await FenceAsync(context, token);
            // A page can be empty after fully paid invoices are skipped; its cursor still
            // advances past the bounded, already scoped IDs that were examined.
            return new(choices.ToImmutable(), selected.Count > limit ? selected[limit - 1]["_id"].AsInt64 : null);
        }, ct);
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

internal sealed record FinancialApprovalMoneySource(BsonDocument InvoiceBalance, BsonDocument? InvoiceHead, BsonArray Receipts,
    IReadOnlyDictionary<long, BsonDocument?> ReceiptHeads, BsonArray Links, Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);

public sealed partial class PaymentAllocationService
{
    internal IMongoDatabase FinancialSourceDatabase => database;
    internal Task FinancialStorageAsync(CancellationToken ct) => StorageAsync(ct);

    private async Task ValidateFinancialSourceAsync(Context context, BsonDocument row, CancellationToken ct) {
        var source = await entitlements.ApprovalSourceAsync(context.Actor, context.Authority, context.Tx,
            row["caseId"].AsInt64, row["returnId"].AsInt64, row["action"].AsString, false, ct);
        R.Equal(row["request"], source.Request); R.Equal(row["invoice"], source.Invoice.MoneySource);
        context.Invoices.Add(source.Invoice.MoneySource); context.Current.Add(source.RequireCurrent);
        context.SourceFences.Add(source.FenceReadAsync);
        await source.FenceReadAsync(ct);
        foreach (var link in row["links"].AsBsonArray) {
            var original = await records.RawAsync(context.Tx, link["originalAllocationId"].AsInt64, ct);
            await ValidateSourceAsync(context, original, ct);
            var verified = await verifications.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, link["verificationId"].AsInt64, false, ct);
            R.Equal(link["verifiedBy"], verified.Decision["actorId"]); R.Equal(link["verificationHash"], verified.Decision["snapshotHash"]);
            R.Equal(link["receiptHash"], verified.Bank.Receipt["snapshotHash"]); R.Equal(link["receiptTotal"], verified.Bank.Receipt["amount"]); R.Equal(link["suspenseAccount"], verified.SuspenseAccount);
            context.AdditionalResources.AddRange(PaymentDraftBson.Scopes(verified.Decision["source"]["scopes"].AsBsonArray)); context.Current.Add(verified.RequireCurrent);
        }
        // The committed event's person evidence is immutable historical provenance. Current
        // authority belongs to this reader; preview/approval/recovery resolve their live makers.
        _ = Require(context);
    }

    // Selected funding is derived from actual immutable allocations. Canonical states are
    // independently reconstructed, and the owning command advances their heads with exact CAS.
    internal async Task<FinancialApprovalMoneySource> FinancialSourcesAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument canonicalInvoice, long[] originalAllocationIds, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || canonicalInvoice is null || originalAllocationIds is null ||
            originalAllocationIds.Length > ReturnFinancialApprovalPolicy.MaximumReleases || originalAllocationIds.Any(id => id <= 0) || originalAllocationIds.Distinct().Count() != originalAllocationIds.Length) throw B.Broken();
        _ = actor.Validate(); originalAllocationIds = [.. originalAllocationIds.Order()];
        var context = new Context(actor, authority, tx, false, financial: true); context.Invoices.Add(canonicalInvoice.DeepClone().AsBsonDocument); _ = Require(context);
        var invoice = await invoices.FinancialEntitlementSourceAsync(actor, authority, tx, canonicalInvoice["invoiceId"].AsInt64, ct);
        R.Equal(invoice.MoneySource, canonicalInvoice); context.Current.Add(invoice.RequireCurrent); context.SourceFences.Add(invoice.FenceAsync);
        var party = PaymentDraftBson.Party(new BsonArray { canonicalInvoice }); var invoiceId = canonicalInvoice["invoiceId"].AsInt64;
        var balance = await records.BalanceAsync(tx, false, invoiceId, party, canonicalInvoice["total"], canonicalInvoice["snapshotHash"], row => ValidateSourceAsync(context, row, ct), ct);
        var receipts = new SortedDictionary<long, BsonDocument>(); var heads = new SortedDictionary<long, BsonDocument?>(); var links = new BsonArray();
        foreach (var id in originalAllocationIds) {
            var original = await records.RawAsync(tx, id, ct); R.Equal(original["party"], party);
            var line = original["lines"].AsBsonArray.SingleOrDefault(value => value["invoiceId"] == invoiceId) ?? throw B.Broken(); R.Equal(line["source"], canonicalInvoice);
            await ValidateSourceAsync(context, original, ct);
            var totals = await records.CorrectionTotalsAsync(tx, id, ct); if (!totals.ContainsKey(invoiceId)) throw B.Broken();
            var receiptId = original["receiptId"].AsInt64;
            if (!receipts.ContainsKey(receiptId)) {
                var receipt = await records.BalanceAsync(tx, true, receiptId, party, original["receiptTotal"], original["receiptHash"], row => ValidateSourceAsync(context, row, ct), ct);
                receipts.Add(receiptId, new() { { "receiptId", receiptId }, { "receiptHash", original["receiptHash"] }, { "receiptTotal", original["receiptTotal"] }, { "before", receipt.Balance } }); heads.Add(receiptId, receipt.Head);
            } else {
                R.Equal(receipts[receiptId]["receiptHash"], original["receiptHash"]); R.Equal(receipts[receiptId]["receiptTotal"], original["receiptTotal"]);
            }
            var verification = await new PaymentVerificationRecords(database).RawAsync(tx, original["verificationId"].AsInt64, ct);
            var link = new BsonDocument { { "originalAllocationId", id }, { "originalAllocationHash", original["snapshotHash"] }, { "originalJournalId", original["journalId"] },
                { "originalJournalFingerprint", original["journalFingerprint"] }, { "allocationActorId", original["actorId"] }, { "verifiedBy", verification["actorId"] } };
            foreach (var field in new[] { "verificationId", "verificationHash", "declarationId", "declarationRevision", "recordedBy", "receiptId", "receiptHash", "receiptTotal", "suspenseAccount" }) link.Add(field, original[field]);
            link.Add("source", line["source"]); link.Add("originallyAllocated", line["amount"]); link.Add("previouslyReleased", new BsonDecimal128(totals[invoiceId])); links.Add(link);
        }
        await RequireNoOrphanJournalAsync(context, party, ct);
        void CurrentSource() { Current(context); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) { CurrentSource(); await FenceAsync(context, token); CurrentSource(); token.ThrowIfCancellationRequested(); }
        CurrentSource(); return new(balance.Balance.DeepClone().AsBsonDocument, balance.Head?.DeepClone().AsBsonDocument,
            new BsonArray(receipts.Values.Select(row => row.DeepClone())), heads.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone().AsBsonDocument),
            links.DeepClone().AsBsonArray, CurrentSource, Fence);
    }
    private async Task RequireNoOrphanFinancialJournalAsync(Context context, BsonDocument party, CancellationToken ct) {
        var filter = new BsonDocument { { "source.namespace", "billing.return-financial" }, { "dimensions.organizationId", party["fulfillmentOrganizationId"] },
            { "dimensions.shopId", party["shopId"] }, { "dimensions.channelId", party["channelId"] }, { "dimensions.buyingOrganizationId", party["buyingOrganizationId"] } };
        using var cursor = await records.Rows("journalEntries").Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var journal in cursor.Current) {
            if (journal["source"]["eventKind"] != "approved") throw B.Broken();
            var row = await records.RawApprovalAsync(context.Tx, journal["source"]["documentId"].AsInt64, ct); R.Equal(row["journalId"], journal["_id"]);
        }
    }
}

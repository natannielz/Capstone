using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
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

    private async Task ValidateFinancialSourceAsync(Context context, BsonDocument row, CancellationToken ct, FinancialEntitlementRequestSource? seededSource = null,
        IReadOnlyDictionary<long, BsonDocument>? capturedOriginals = null) {
        var source = seededSource ?? await entitlements.ApprovalSourceAsync(context.Actor, context.Authority, context.Tx,
            row["caseId"].AsInt64, row["returnId"].AsInt64, row["action"].AsString, false, ct);
        if (seededSource is not null) {
            source.RequireContext(context.Actor, context.Authority, context.Tx);
            R.Equal(row["caseId"], source.Request["caseId"]); R.Equal(row["returnId"], source.Request["_id"]);
            R.Equal(row["invoiceId"], source.Request["invoiceId"]); R.Equal(row["action"], source.Request["action"]);
            R.Equal(row["request"]["snapshotHash"], source.Request["snapshotHash"]);
        }
        R.Equal(row["request"], source.Request); R.Equal(row["invoice"], source.Invoice.MoneySource);
        if (seededSource is null) {
            context.Invoices.Add(source.Invoice.MoneySource); context.Current.Add(source.RequireCurrent);
            context.SourceFences.Add(source.FenceReadAsync); await source.FenceReadAsync(ct);
        }
        // The seeded path's actual request/pool/return/issued-invoice full fence is already
        // registered once by FinancialSourcesAsync; other historical requests capture normally.
        foreach (var link in row["links"].AsBsonArray) {
            var originalId = link["originalAllocationId"].AsInt64;
            var original = capturedOriginals is not null && capturedOriginals.TryGetValue(originalId, out var actualOriginal)
                ? actualOriginal.DeepClone().AsBsonDocument : await records.RawAsync(context.Tx, originalId, ct);
            await ValidateSourceAsync(context, original, ct);
            if (seededSource is not null) {
                // RawApproval proves this original link, and ValidateSourceAsync has already
                // captured its actual scoped verification/bank provenance and current checks.
                foreach (var field in new[] { "verificationId", "verificationHash", "receiptId", "receiptHash", "receiptTotal", "suspenseAccount" }) R.Equal(link[field], original[field]);
                R.Equal(link["allocationActorId"], original["actorId"]);
                var verified = await new PaymentVerificationRecords(database).RawAsync(context.Tx, original["verificationId"].AsInt64, ct);
                R.Equal(link["verifiedBy"], verified["actorId"]); R.Equal(link["verificationHash"], verified["snapshotHash"]);
            } else {
                var verified = await verifications.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, link["verificationId"].AsInt64, false, ct);
                R.Equal(link["verifiedBy"], verified.Decision["actorId"]); R.Equal(link["verificationHash"], verified.Decision["snapshotHash"]);
                R.Equal(link["receiptHash"], verified.Bank.Receipt["snapshotHash"]); R.Equal(link["receiptTotal"], verified.Bank.Receipt["amount"]); R.Equal(link["suspenseAccount"], verified.SuspenseAccount);
                context.AdditionalResources.AddRange(PaymentDraftBson.Scopes(verified.Decision["source"]["scopes"].AsBsonArray)); context.Current.Add(verified.RequireCurrent);
            }
        }
        // The committed event's person evidence is immutable historical provenance. Current
        // authority belongs to this reader; preview/approval/recovery resolve their live makers.
        _ = Require(context);
    }

    // Selected funding is derived from actual immutable allocations. Canonical states are
    // independently reconstructed, and the owning command advances their heads with exact CAS.
    internal Task<FinancialApprovalMoneySource> FinancialSourcesAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument canonicalInvoice, long[] originalAllocationIds, CancellationToken ct) =>
        FinancialSourcesAsync(actor, authority, tx, canonicalInvoice, originalAllocationIds, null, ct);

    internal async Task<FinancialApprovalMoneySource> FinancialSourcesAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument canonicalInvoice, long[] originalAllocationIds, FinancialEntitlementRequestSource? seededSource, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || canonicalInvoice is null || originalAllocationIds is null ||
            originalAllocationIds.Length > ReturnFinancialApprovalPolicy.MaximumReleases || originalAllocationIds.Any(id => id <= 0) || originalAllocationIds.Distinct().Count() != originalAllocationIds.Length) throw B.Broken();
        _ = actor.Validate(); originalAllocationIds = [.. originalAllocationIds.Order()];
        seededSource?.RequireContext(actor, authority, tx);
        var context = new Context(actor, authority, tx, false, financial: true, seededSource: seededSource); context.Invoices.Add(canonicalInvoice.DeepClone().AsBsonDocument); _ = Require(context);
        var invoice = seededSource?.Invoice ?? await invoices.FinancialEntitlementSourceAsync(actor, authority, tx, canonicalInvoice["invoiceId"].AsInt64, ct);
        R.Equal(invoice.MoneySource, canonicalInvoice);
        if (seededSource is null) { context.Current.Add(invoice.RequireCurrent); context.SourceFences.Add(invoice.FenceAsync); }
        else {
            R.Equal(seededSource.Request["source"]["invoice"], canonicalInvoice);
            R.Equal(seededSource.Request["invoiceId"], invoice.Invoice["_id"]);
            R.Equal(canonicalInvoice["invoiceId"], invoice.Invoice["_id"]); R.Equal(canonicalInvoice["snapshotHash"], invoice.Invoice["snapshotHash"]);
            R.Equal(canonicalInvoice["scopes"], seededSource.Request["source"]["invoice"]["scopes"]);
            R.Equal(seededSource.Request["source"]["issuedLines"], ReturnEntitlementBson.IssuedLines(invoice.Invoice));
            R.Equal(seededSource.Request["source"]["originalAccounts"], invoice.OriginalAccounts);
            context.Current.Add(seededSource.RequireCurrent); context.SourceFences.Add(seededSource.FenceReadAsync);
        }
        // These are actual owner-validated rows/totals from this admission, never a cross-
        // transaction cache. The final money fence/recheck still rereads every actual event.
        var capturedOriginals = new Dictionary<long, BsonDocument>(); var capturedReleaseTotals = new Dictionary<long, Dictionary<long, decimal>>();
        async Task ValidateCapturedSourceAsync(BsonDocument row) {
            if (seededSource is null || row["operation"] != A.Operation || row["caseId"] != seededSource.Request["caseId"] ||
                row["returnId"] != seededSource.Request["_id"] || row["action"] != seededSource.Request["action"]) {
                await ValidateSourceAsync(context, row, ct);
                if (seededSource is not null && row["operation"] == B.Operation) capturedOriginals[row["_id"].AsInt64] = row.DeepClone().AsBsonDocument;
                return;
            }
            if (!context.Validated.Add(row["_id"].AsInt64)) return;
            await ValidateFinancialSourceAsync(context, row, ct, seededSource, capturedOriginals);
        }
        var party = PaymentDraftBson.Party(new BsonArray { canonicalInvoice }); var invoiceId = canonicalInvoice["invoiceId"].AsInt64;
        var balance = await records.BalanceAsync(tx, false, invoiceId, party, canonicalInvoice["total"], canonicalInvoice["snapshotHash"], ValidateCapturedSourceAsync, ct,
            seededSource is null ? null : capturedReleaseTotals);
        var receipts = new SortedDictionary<long, BsonDocument>(); var heads = new SortedDictionary<long, BsonDocument?>(); var links = new BsonArray();
        foreach (var id in originalAllocationIds) {
            var original = capturedOriginals.TryGetValue(id, out var actualOriginal) ? actualOriginal.DeepClone().AsBsonDocument : await records.RawAsync(tx, id, ct);
            R.Equal(original["party"], party);
            var line = original["lines"].AsBsonArray.SingleOrDefault(value => value["invoiceId"] == invoiceId) ?? throw B.Broken(); R.Equal(line["source"], canonicalInvoice);
            await ValidateCapturedSourceAsync(original);
            var receiptId = original["receiptId"].AsInt64;
            if (!receipts.ContainsKey(receiptId)) {
                var receipt = await records.BalanceAsync(tx, true, receiptId, party, original["receiptTotal"], original["receiptHash"], ValidateCapturedSourceAsync, ct,
                    seededSource is null ? null : capturedReleaseTotals);
                receipts.Add(receiptId, new() { { "receiptId", receiptId }, { "receiptHash", original["receiptHash"] }, { "receiptTotal", original["receiptTotal"] }, { "before", receipt.Balance } }); heads.Add(receiptId, receipt.Head);
            } else {
                R.Equal(receipts[receiptId]["receiptHash"], original["receiptHash"]); R.Equal(receipts[receiptId]["receiptTotal"], original["receiptTotal"]);
            }
            var totals = capturedReleaseTotals.TryGetValue(id, out var actualTotals) ? actualTotals : await records.CorrectionTotalsAsync(tx, id, ct);
            if (!totals.ContainsKey(invoiceId)) throw B.Broken();
            var verification = await new PaymentVerificationRecords(database).RawAsync(tx, original["verificationId"].AsInt64, ct);
            var link = new BsonDocument { { "originalAllocationId", id }, { "originalAllocationHash", original["snapshotHash"] }, { "originalJournalId", original["journalId"] },
                { "originalJournalFingerprint", original["journalFingerprint"] }, { "allocationActorId", original["actorId"] }, { "verifiedBy", verification["actorId"] } };
            foreach (var field in new[] { "verificationId", "verificationHash", "declarationId", "declarationRevision", "recordedBy", "receiptId", "receiptHash", "receiptTotal", "suspenseAccount" }) link.Add(field, original[field]);
            link.Add("source", line["source"]); link.Add("originallyAllocated", line["amount"]); link.Add("previouslyReleased", new BsonDecimal128(totals[invoiceId])); links.Add(link);
        }
        await RequireNoOrphanJournalAsync(context, party, ct);
        void CurrentSource() { seededSource?.RequireContext(actor, authority, tx); Current(context); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) { CurrentSource(); await FenceAsync(context, token); CurrentSource(); token.ThrowIfCancellationRequested(); }
        CurrentSource(); return new(balance.Balance.DeepClone().AsBsonDocument, balance.Head?.DeepClone().AsBsonDocument,
            new BsonArray(receipts.Values.Select(row => row.DeepClone())), heads.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone().AsBsonDocument),
            links.DeepClone().AsBsonArray, CurrentSource, Fence);
    }

    // The caller must first complete the captured owner's source and money full fences in
    // this transaction. This recheck reconstructs money independently without recapturing them.
    internal async Task RecheckFinancialBalancesAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument canonicalInvoice, FinancialApprovalMoneySource captured, CancellationToken ct) {
        if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion || canonicalInvoice is null || captured is null) throw B.Broken();
        _ = actor.Validate();
        var resources = PaymentDraftBson.Scopes(canonicalInvoice["scopes"].AsBsonArray);
        void CurrentProof() {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
            captured.RequireCurrent();
            var query = ScopePolicy.Compile(actor, authority, Permissions.InvoiceRead, clock.UtcNow); AuthorizationStore.Require(query);
            if (resources.Length == 0 || resources.Any(resource => !ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource))) throw B.Broken();
            ct.ThrowIfCancellationRequested();
        }
        CurrentProof();
        var party = PaymentDraftBson.Party(new BsonArray { canonicalInvoice }); var invoiceId = canonicalInvoice["invoiceId"].AsInt64;
        if (invoiceId <= 0 || captured.Receipts.Count > ReturnFinancialApprovalPolicy.MaximumReleases) throw B.Broken();
        var receiptIds = captured.Receipts.Select(receipt => receipt["receiptId"].AsInt64).ToArray();
        if (receiptIds.Any(id => id <= 0) || !receiptIds.SequenceEqual(receiptIds.Distinct().Order()) ||
            !captured.ReceiptHeads.Keys.Order().SequenceEqual(receiptIds)) throw B.Broken();
        var allocationIds = captured.Links.Select(link => link["originalAllocationId"].AsInt64).ToArray();
        if (allocationIds.Length > ReturnFinancialApprovalPolicy.MaximumReleases || allocationIds.Any(id => id <= 0) ||
            !allocationIds.SequenceEqual(allocationIds.Distinct().Order()) ||
            !captured.Links.Select(link => link["receiptId"].AsInt64).Distinct().Order().SequenceEqual(receiptIds)) throw B.Broken();
        foreach (var link in captured.Links) {
            R.Equal(link["source"], canonicalInvoice);
            var receipt = captured.Receipts.Single(value => value["receiptId"] == link["receiptId"]);
            R.Equal(link["receiptHash"], receipt["receiptHash"]); R.Equal(link["receiptTotal"], receipt["receiptTotal"]);
        }
        // Captured source/money full fences already revalidated all source scopes and
        // provenance in this transaction. BalanceAsync still validates actual B/C/A events,
        // effects, journals, chronology, source hashes and complete canonical heads.
        var invoice = await records.BalanceAsync(tx, false, invoiceId, party, canonicalInvoice["total"], canonicalInvoice["snapshotHash"],
            _ => Task.CompletedTask, ct);
        R.Equal(invoice.Balance, captured.InvoiceBalance);
        if ((invoice.Head is null) != (captured.InvoiceHead is null)) throw B.Broken();
        if (invoice.Head is not null) R.Equal(invoice.Head, captured.InvoiceHead!);
        CurrentProof();
        var actualReceipts = new BsonArray();
        foreach (var receipt in captured.Receipts) {
            var id = receipt["receiptId"].AsInt64;
            var actual = await records.BalanceAsync(tx, true, id, party, receipt["receiptTotal"], receipt["receiptHash"],
                _ => Task.CompletedTask, ct);
            var head = captured.ReceiptHeads[id]; if ((actual.Head is null) != (head is null)) throw B.Broken();
            if (actual.Head is not null) R.Equal(actual.Head, head!);
            actualReceipts.Add(new BsonDocument { { "receiptId", id }, { "receiptHash", receipt["receiptHash"] },
                { "receiptTotal", receipt["receiptTotal"] }, { "before", actual.Balance } });
            CurrentProof();
        }
        R.Equal(actualReceipts, captured.Receipts);
        var context = new Context(actor, authority, tx, false, financial: true); context.Invoices.Add(canonicalInvoice.DeepClone().AsBsonDocument);
        context.Current.Add(captured.RequireCurrent); Current(context);
        await RequireNoOrphanJournalAsync(context, party, ct); CurrentProof();
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

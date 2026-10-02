using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentAllocationView(BsonDocument Verification, BsonDocument Receipt, BsonDocument ReceiptBalance, BsonArray Invoices);
public sealed record PaymentAllocationPreview(string Binding, PaymentAllocationView View, BsonDocument Policy, PaymentAllocationResult Result);

public sealed partial class PaymentAllocationService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly PaymentVerificationService verifications;
    private readonly InvoiceService invoices;
    private readonly ReturnEntitlementService entitlements;
    private readonly R records;
    public PaymentAllocationService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PaymentVerificationService verifications, InvoiceService invoices, ReturnEntitlementService entitlements) {
        foreach (var owner in new[] { verifications.AllocationDatabase, invoices.PaymentSourceDatabase, entitlements.ApprovalSourceDatabase })
            if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw new ArgumentException("Allocation owners must share one transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.verifications = verifications; this.invoices = invoices; this.entitlements = entitlements; authorization = new(database, unit, clock); records = new(database, clock);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, bool write, bool correction = false, bool financial = false,
        FinancialEntitlementRequestSource? seededSource = null) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal bool Write { get; } = write;
        internal bool Correction { get; } = correction;
        internal bool Financial { get; } = financial;
        // Only FinancialSourcesAsync supplies this actual same-transaction owner proof;
        // its full issued-invoice fence is registered once by that entry point.
        internal FinancialEntitlementRequestSource? SeededSource { get; } = seededSource;
        internal PaymentAllocationSource Source = null!;
        internal readonly List<BsonDocument> Invoices = [];
        internal readonly List<Action> Current = [];
        internal readonly List<Func<CancellationToken, Task>> SourceFences = [];
        internal readonly HashSet<long> Validated = [];
        internal readonly List<ResourceScope> AdditionalResources = [];
        internal BsonDocument? Policy;
        internal BsonDocument? CorrectionPolicy;
        internal ResourceScope[] Resources => [.. (Source is null ? [] : PaymentDraftBson.Scopes(Source.Decision["source"]["scopes"].AsBsonArray))
            .Concat(Invoices.SelectMany(source => PaymentDraftBson.Scopes(source["scopes"].AsBsonArray))).Concat(AdditionalResources).Distinct()];
    }
    private sealed record Prepared(Context Context, BsonDocument ReceiptBalance, BsonDocument? ReceiptHead,
        BsonArray Lines, IReadOnlyDictionary<long, BsonDocument?> InvoiceHeads) {
        internal PaymentAllocationView View => new(Context.Source.Decision, Context.Source.Bank.Receipt, ReceiptBalance,
            new(Lines.Select(line => new BsonDocument { { "source", line["source"] }, { "balance", line["before"] } })));
    }
    private AuthorizationQuery[] Require(Context context) => (context.Write ? new[] { Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentAllocate }.Concat(context.Correction ? [Permissions.JournalReverse] : []) : context.Financial ? [Permissions.InvoiceRead] : [Permissions.PaymentReviewRead, Permissions.InvoiceRead]).Select(permission => {
        var query = ScopePolicy.Compile(context.Actor, context.Authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (context.Resources.Any(resource => !ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource))) throw B.Broken();
        return query;
    }).ToArray();
    private void Current(Context context) {
        _ = Require(context); foreach (var current in context.Current) current();
        if (context.Policy is { } policy) R.PolicyAt(policy, Now);
        if (context.CorrectionPolicy is { } correctionPolicy) C.PolicyAt(correctionPolicy, Now);
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        foreach (var fence in context.SourceFences) await fence(ct);
        var queries = Require(context); var resources = context.Resources;
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, context.Authority.Directory))).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.Actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId, context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task StorageAsync(CancellationToken ct) {
        await verifications.AllocationStorageAsync(ct); await invoices.PaymentStorageAsync(ct); await PaymentAllocationStorage.RequireAsync(database, ct);
        await entitlements.ApprovalStorageAsync(ct); await PaymentAllocationStorage.RequireFinancialAsync(database, ct);
        await PaymentAllocationStorage.RequireSettlementsAsync(database, ct);
    }
    private async Task SourceAsync(Context context, long id, bool newEffect, CancellationToken ct) {
        _ = Require(context); context.Source = await verifications.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, id, newEffect, ct);
        context.AdditionalResources.AddRange(PaymentDraftBson.Scopes(context.Source.Decision["source"]["scopes"].AsBsonArray));
        context.Current.Add(context.Source.RequireCurrent); _ = Require(context);
    }
    private async Task<BsonDocument> PolicyAsync(Context context, CancellationToken ct) {
        var bank = context.Source.Bank.Receipt;
        var raw = await records.Rows(B.Policies).Find(context.Tx, new BsonDocument("scope", bank["scope"])).SingleOrDefaultAsync(ct) ?? throw B.Changed("payment_allocation_policy_unavailable");
        var policy = await records.RowAsync(context.Tx, B.Policies, raw["_id"].AsInt64, ct); R.PolicyAt(policy, Now);
        R.Equal(policy["suspenseMapping"], bank["policy"]["suspenseMapping"]);
        await records.TouchAsync(context.Tx, B.Policies, policy, ct); context.Policy = B.Snapshot(policy); return context.Policy;
    }
    private async Task ValidateSourceAsync(Context context, BsonDocument row, CancellationToken ct) {
        if (!context.Validated.Add(row["_id"].AsInt64)) return;
        if (row["operation"] == D.Operation) { await ValidateSettlementSourceAsync(context, row, ct); return; }
        if (row["operation"] == A.Operation) { await ValidateFinancialSourceAsync(context, row, ct); return; }
        if (row["operation"] != B.Operation && row["operation"] != C.Operation) throw B.Broken();
        var source = await verifications.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, row["verificationId"].AsInt64, false, ct);
        context.AdditionalResources.AddRange(PaymentDraftBson.Scopes(source.Decision["source"]["scopes"].AsBsonArray));
        context.Current.Add(source.RequireCurrent);
        R.Equal(row["verificationHash"], source.Decision["snapshotHash"]); R.Equal(row["receiptId"], source.Bank.Receipt["_id"]);
        R.Equal(row["receiptHash"], source.Bank.Receipt["snapshotHash"]); R.Equal(row["receiptTotal"], source.Bank.Receipt["amount"]);
        R.Equal(row["suspenseAccount"], source.SuspenseAccount);
        R.Equal(row["declarationId"], source.Decision["declarationId"]); R.Equal(row["declarationRevision"], source.Decision["source"]["revision"]); R.Equal(row["recordedBy"], source.Decision["source"]["recordedBy"]);
        R.Equal(row["party"], source.Decision["source"]["party"]); R.Equal(row["policy"]["scope"], source.Bank.Receipt["scope"]);
        R.Equal(row["policy"]["suspenseMapping"], source.Bank.Receipt["policy"]["suspenseMapping"]);
        foreach (var line in row["lines"].AsBsonArray) {
            BsonDocument invoice;
            if (context.SeededSource is { } seed && line["invoiceId"] == seed.Invoice.MoneySource["invoiceId"]) {
                seed.RequireContext(context.Actor, context.Authority, context.Tx);
                invoice = seed.Invoice.MoneySource; R.Equal(line["source"], invoice);
            } else invoice = await invoices.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, line["invoiceId"].AsInt64, ct);
            context.Invoices.Add(invoice); R.Equal(line["source"], invoice);
        }
        R.Equal(row["binding"], row["operation"] == C.Operation ? C.Binding(await records.RawAsync(context.Tx, row["originalAllocationId"].AsInt64, ct), row["correctionPolicy"].AsBsonDocument, row["policy"].AsBsonDocument, row["receiptBefore"].AsBsonDocument, row["lines"].AsBsonArray) : B.Binding(row["verificationHash"], row["receiptHash"], row["policy"].AsBsonDocument, row["receiptBefore"].AsBsonDocument, row["lines"].AsBsonArray));
        _ = Require(context);
    }
    private async Task<Prepared> PrepareAsync(Context context, long verificationId, long[] invoiceIds, bool newEffect, CancellationToken ct) {
        if (verificationId <= 0 || invoiceIds.Length is < 1 or > PaymentAllocationPolicy.MaximumInvoices || invoiceIds.Any(id => id <= 0) || invoiceIds.Distinct().Count() != invoiceIds.Length) throw PaymentAllocationPolicy.Invalid();
        await SourceAsync(context, verificationId, newEffect, ct);
        var party = context.Source.Decision["source"]["party"].AsBsonDocument; var receipt = context.Source.Bank.Receipt;
        var balance = await records.BalanceAsync(context.Tx, true, receipt["_id"].AsInt64, party, receipt["amount"], receipt["snapshotHash"], row => ValidateSourceAsync(context, row, ct), ct);
        BsonArray lines = []; var heads = new Dictionary<long, BsonDocument?>();
        foreach (var id in invoiceIds.Order()) {
            var invoice = await invoices.AllocationSourceAsync(context.Actor, context.Authority, context.Tx, id, ct); context.Invoices.Add(invoice); _ = Require(context);
            if (!PaymentDraftBson.Party(new BsonArray { invoice }).Equals(party)) throw PaymentAllocationPolicy.Invalid("payment_allocation_party_mismatch");
            var funds = await records.BalanceAsync(context.Tx, false, id, party, invoice["total"], invoice["snapshotHash"], row => ValidateSourceAsync(context, row, ct), ct);
            lines.Add(new BsonDocument { { "invoiceId", id }, { "source", invoice }, { "before", funds.Balance } }); heads.Add(id, funds.Head);
        }
        await RequireNoOrphanJournalAsync(context, party, ct);
        return new(context, balance.Balance, balance.Head, lines, heads);
    }
    private PaymentAllocationResult Review(Prepared prepared, PaymentAllocationInput input) {
        var party = B.Party(prepared.Context.Source.Decision["source"]["party"].AsBsonDocument); var receipt = prepared.Context.Source.Bank.Receipt; var before = prepared.ReceiptBalance;
        var result = PaymentAllocationPolicy.Review(new(new(receipt["_id"].AsInt64), before["version"].AsInt64, party, "IDR", InvoiceBson.Money(receipt["amount"]), InvoiceBson.Money(before["allocated"]), InvoiceBson.Money(before["reservedForRefund"])),
            [.. prepared.Lines.Select(line => new InvoiceFunds(new(line["invoiceId"].AsInt64), line["before"]["version"].AsInt64, party, "IDR", InvoiceBson.Money(line["source"]["total"]), InvoiceBson.Money(line["before"]["paid"]), InvoiceBson.Money(line["before"]["credited"])))], input);
        foreach (var line in prepared.Lines) {
            if (prepared.Context.Policy is { } policy) R.Equal(line["source"]["arMapping"], policy["arMapping"]);
            var amount = input.Lines.Single(value => value.InvoiceId.Value == line["invoiceId"].AsInt64).Amount;
            line["amount"] = InvoiceBson.Money(amount); line["version"] = line["before"]["version"].AsInt64 + 1;
            line["remaining"] = InvoiceBson.Money(result.Invoices.Single(value => value.InvoiceId.Value == line["invoiceId"].AsInt64).Remaining);
        }
        return result;
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128) || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw PaymentAllocationPolicy.Invalid();
    }
    private async Task RequireNoOrphanJournalAsync(Context context, BsonDocument party, CancellationToken ct) {
        var filter = new BsonDocument { { "source.namespace", "billing.payment-allocation" }, { "dimensions.organizationId", party["fulfillmentOrganizationId"] },
            { "dimensions.shopId", party["shopId"] }, { "dimensions.channelId", party["channelId"] }, { "dimensions.buyingOrganizationId", party["buyingOrganizationId"] } };
        using var cursor = await records.Rows("journalEntries").Find(context.Tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var journal in cursor.Current) {
            var type = journal["source"]["eventKind"].AsString; if (type is not ("allocated" or "unallocated")) throw B.Broken();
            var row = await records.RawEventAsync(context.Tx, type == "unallocated", journal["source"]["documentId"].AsInt64, ct);
            R.Equal(row["journalId"], journal["_id"]);
        }
        await RequireNoOrphanFinancialJournalAsync(context, party, ct);
        await RequireNoOrphanSettlementJournalAsync(context, party, ct);
    }
    private async Task AuditReadAsync(Context context, string action, long? verificationId, CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument {
        { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", B.Prefix + "read" }, { "operation", B.Prefix + action },
        { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "verificationId", verificationId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "createdAt", Now.UtcDateTime }
    }, cancellationToken: ct);
    public async Task<PaymentAllocationView> ReadAsync(ActorContext actor, long verificationId, long[] invoiceIds, CancellationToken ct = default) {
        invoiceIds = [.. invoiceIds]; await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var prepared = await PrepareAsync(new(actor, authority, tx, false), verificationId, invoiceIds, false, token);
            await AuditReadAsync(prepared.Context, "balances", verificationId, token); await FenceAsync(prepared.Context, token); return prepared.View;
        }, ct);
    }
    public async Task<PaymentAllocationPreview> PreviewAsync(ActorContext actor, long verificationId, PaymentAllocationInput input, CancellationToken ct = default) {
        input = PaymentAllocationPolicy.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var prepared = await PrepareAsync(new(actor, authority, tx, true), verificationId, [.. input.Lines.Select(line => line.InvoiceId.Value)], true, token);
            var policy = await PolicyAsync(prepared.Context, token); var result = Review(prepared, input);
            var binding = B.Binding(prepared.Context.Source.Decision["snapshotHash"], prepared.Context.Source.Bank.Receipt["snapshotHash"], policy, prepared.ReceiptBalance, prepared.Lines);
            await AuditReadAsync(prepared.Context, "preview", verificationId, token); await FenceAsync(prepared.Context, token); return new PaymentAllocationPreview(binding, prepared.View, policy, result);
        }, ct);
    }
}

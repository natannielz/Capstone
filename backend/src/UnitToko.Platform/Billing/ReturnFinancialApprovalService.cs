using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;
public sealed record ReturnFinancialApprovalView(BsonDocument Request, BsonDocument InvoiceBalance, BsonArray Receipts, BsonArray Links, bool HasMore, BsonDocument? Approval);
public sealed record ReturnFinancialApprovalPreview(ReturnFinancialApprovalView View, string Binding, BsonDocument Policy, ReturnFinancialApprovalResult Result);

/// <summary>Consumes a real inspected financial request once. Credits, linked allocation releases,
/// refund liability, canonical balances and effects commit together; this command cannot pay money.</summary>
public sealed partial class ReturnFinancialApprovalService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly ReturnEntitlementService entitlements;
    private readonly PaymentAllocationService allocations;
    private readonly R records;
    public ReturnFinancialApprovalService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ReturnEntitlementService entitlements, PaymentAllocationService allocations) {
        foreach (var owner in new[] { entitlements.ApprovalSourceDatabase, allocations.FinancialSourceDatabase })
            if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw new ArgumentException("Financial owners must share one transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.entitlements = entitlements; this.allocations = allocations; records = new(database, clock); authorization = new(database, unit, clock);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Frame(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, bool write, FinancialEntitlementRequestSource source, FinancialApprovalMoneySource money) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal bool Write = write;
        internal FinancialEntitlementRequestSource Source = source;
        internal FinancialApprovalMoneySource Money = money;
        internal BsonDocument? Policy;
        internal PersonAuthorityProof? People;
        internal string Action => Source.Request["action"].AsString;
        internal ResourceScope[] Resources => PaymentDraftBson.Scopes(Source.Invoice.MoneySource["scopes"].AsBsonArray);
        internal string Permission => Action == "credit" ? Permissions.CreditApprove : Permissions.RefundApprove;
    }
    private AuthorizationQuery[] Require(Frame frame) {
        if (frame.Write && (frame.Actor.Assurance is not (FactorAssurance.MultiFactor or FactorAssurance.PhishingResistant) || frame.Actor.FactorCompletedAt > clock.UtcNow || clock.UtcNow - frame.Actor.FactorCompletedAt > TimeSpan.FromMinutes(5)))
            throw new AuthorizationDeniedException("step_up_required");
        return new[] { Permissions.CaseRead, Permissions.InvoiceRead, frame.Permission }.Concat(frame.Action == "refund" ? [Permissions.CreditApprove, Permissions.PaymentReviewRead] : []).Distinct().Select(permission => {
            var query = ScopePolicy.Compile(frame.Actor, frame.Authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (frame.Resources.Length == 0 || frame.Resources.Any(resource => !ScopePolicy.IsResourceActive(resource, frame.Authority.Directory) || !query.Matches(resource))) throw B.Broken();
            return query;
        }).ToArray();
    }
    private void Current(Frame frame) {
        _ = Require(frame); frame.Source.RequireCurrent(); frame.Money.RequireCurrent(); frame.People?.RequireCurrent(frame.Tx, clock.UtcNow);
        if (frame.Policy is { } policy) A.PolicyAt(policy, Now);
    }
    private async Task FenceAsync(Frame frame, CancellationToken ct) {
        await frame.Source.FenceReadAsync(ct); await frame.Money.FenceAsync(ct);
        var queries = Require(frame); var resources = frame.Resources;
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(frame.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, frame.Authority.Directory))).Distinct(), [],
            frame.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), frame.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), frame.Tx, ct);
        if (frame.Actor.Session is { } session) {
            var live = await records.Rows("identitySessions").Find(frame.Tx, SessionPersistence.LiveFilter(session, frame.Actor.UserId, frame.Actor.ProviderIssuer, frame.Actor.ProviderSubject, frame.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null || live["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || live["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        Current(frame); ct.ThrowIfCancellationRequested();
    }
    private async Task StorageAsync(CancellationToken ct) {
        await entitlements.ApprovalStorageAsync(ct); await allocations.FinancialStorageAsync(ct); await ReturnFinancialApprovalStorage.RequireAsync(database, ct);
    }
    private async Task<Frame> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, bool write, long caseId, long returnId, string action, long[] originalAllocationIds, CancellationToken ct) {
        var source = await entitlements.ApprovalSourceAsync(actor, authority, tx, caseId, returnId, action, write, ct);
        var money = await allocations.FinancialSourcesAsync(actor, authority, tx, source.Invoice.MoneySource, originalAllocationIds, ct);
        var frame = new Frame(actor, authority, tx, write, source, money); _ = Require(frame); return frame;
    }
    private async Task PeopleAsync(Frame frame, CancellationToken ct) {
        var request = frame.Source.Request;
        var makers = new[] { request["actorId"].AsInt64, request["source"]["requestedBy"].AsInt64 }.Concat(frame.Money.Links.SelectMany(link => new[] { link["allocationActorId"].AsInt64, link["verifiedBy"].AsInt64, link["recordedBy"].AsInt64 })).Distinct().ToArray();
        var users = request["source"]["participants"].AsBsonArray.Select(value => value.AsInt64).Concat(makers).Append(frame.Actor.UserId).Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(frame.Tx, [.. users], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == frame.Actor.UserId), makers.Select(id => proof.Evidence.Single(person => person.UserId == id)).ToArray()))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(frame.Tx, proof, ct); frame.People = proof; Current(frame);
    }
    private async Task<BsonDocument> PolicyAsync(Frame frame, CancellationToken ct) {
        var party = PaymentDraftBson.Party(new BsonArray { frame.Source.Invoice.MoneySource });
        var scope = new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } };
        var policy = await records.Rows(A.Policies).Find(frame.Tx, new BsonDocument("$and", new BsonArray { new BsonDocument("scope", scope), ReturnFinancialApprovalSchemas.Validator(A.Policies) })).SingleOrDefaultAsync(ct) ?? throw B.Changed("return_financial_policy_unavailable");
        A.PolicyAt(policy, Now);
        R.Equal(policy["arMapping"], frame.Source.Invoice.MoneySource["arMapping"]);
        if (frame.Source.Invoice.OriginalAccounts.Any(account => account["mappingKey"] == policy["refundLiabilityMapping"])) throw B.Broken();
        await records.TouchAsync(frame.Tx, A.Policies, policy, ct); frame.Policy = B.Snapshot(policy); return frame.Policy;
    }
    private async Task<BsonDocument?> SavedAsync(Frame frame, CancellationToken ct) {
        var raw = await records.Rows(A.Events).Find(frame.Tx, new BsonDocument("returnId", frame.Source.Request["_id"])).SingleOrDefaultAsync(ct);
        if (raw is null) {
            foreach (var (table, field, prefix) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
                if (await records.Rows(table).Find(frame.Tx, new BsonDocument { { field, A.Operation }, { prefix + ".returnId", frame.Source.Request["_id"] } }).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var row = await records.RawApprovalAsync(frame.Tx, raw["_id"].AsInt64, ct); R.Equal(row["request"], frame.Source.Request); R.Equal(row["invoice"], frame.Source.Invoice.MoneySource);
        if (row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken(); return row;
    }
    private async Task<BsonDocument?> ForKeyAsync(Frame frame, string key, CancellationToken ct) {
        var row = await records.Rows(A.Events).Find(frame.Tx, new BsonDocument { { "actorId", frame.Actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) {
            var saved = await SavedAsync(frame, ct) ?? throw B.Broken(); R.Equal(saved["_id"], row["_id"]); return saved;
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await records.Rows(table).Find(frame.Tx, new BsonDocument { { "actorId", frame.Actor.UserId }, { field, A.Operation }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    private static BsonDocument ReviewRow(Frame frame, BsonDocument policy, ReturnFinancialApprovalInput input) {
        input = ReturnFinancialApprovalPolicy.Copy(input); var links = frame.Money.Links.DeepClone().AsBsonArray; var receipts = frame.Money.Receipts.DeepClone().AsBsonArray;
        foreach (var link in links) link["amount"] = InvoiceBson.Money(input.Releases.Single(value => value.OriginalAllocationId.Value == link["originalAllocationId"].AsInt64).Amount);
        foreach (var receipt in receipts) receipt["released"] = new BsonDecimal128(links.Where(link => link["receiptId"] == receipt["receiptId"]).Sum(link => link["amount"].AsDecimal));
        var request = frame.Source.Request;
        var row = new BsonDocument { { "request", request }, { "caseId", request["caseId"] }, { "returnId", request["_id"] }, { "invoiceId", request["invoiceId"] }, { "action", request["action"] },
            { "invoice", frame.Source.Invoice.MoneySource }, { "party", PaymentDraftBson.Party(new BsonArray { frame.Source.Invoice.MoneySource }) },
            { "invoiceBefore", frame.Money.InvoiceBalance }, { "invoiceVersion", frame.Money.InvoiceBalance["version"].AsInt64 + 1 }, { "receipts", receipts }, { "links", links },
            { "input", A.Input(input) }, { "policy", policy }, { "credit", request["credit"] } };
        var review = A.Review(row); row.Add("reservedRefund", InvoiceBson.Money(review.ReservedRefund)); row.Add("remainingInvoice", InvoiceBson.Money(review.RemainingInvoice));
        foreach (var receipt in receipts) {
            var result = review.Receipts.Single(value => value.After.ReceiptId.Value == receipt["receiptId"].AsInt64);
            receipt["version"] = result.After.Version; receipt["remainingReceipt"] = InvoiceBson.Money(result.Available);
        }
        return row;
    }
    private async Task AuditReadAsync(Frame frame, string action, CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(frame.Tx, new BsonDocument {
        { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", "billing.return.financial.read" }, { "operation", "billing.return.financial." + action },
        { "actorId", frame.Actor.UserId }, { "actorGrantVersion", frame.Actor.GrantVersion }, { "returnId", frame.Source.Request["_id"] }, { "createdAt", Now.UtcDateTime }
    }, cancellationToken: ct);
    public async Task<ReturnFinancialApprovalView> ReadAsync(ActorContext actor, long caseId, long returnId, string action, long? beforeAllocationId = null, CancellationToken ct = default) {
        if (beforeAllocationId is <= 0) throw ReturnFinancialApprovalPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await entitlements.ApprovalSourceAsync(actor, authority, tx, caseId, returnId, action, false, token);
            var filter = new BsonDocument("lines.invoiceId", source.Request["invoiceId"]);
            if (beforeAllocationId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var candidates = action == "refund" ? await records.Rows(B.Events).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(101).ToListAsync(token) : [];
            var frame = await PrepareAsync(actor, authority, tx, false, caseId, returnId, action, [.. candidates.Take(100).Select(row => row["_id"].AsInt64)], token);
            var saved = await SavedAsync(frame, token); await AuditReadAsync(frame, "query", token); await FenceAsync(frame, token);
            return new ReturnFinancialApprovalView(frame.Source.Request, frame.Money.InvoiceBalance, frame.Money.Receipts, frame.Money.Links, candidates.Count > 100, saved is null ? null : A.Result(saved));
        }, ct);
    }
    public async Task<ReturnFinancialApprovalPreview> PreviewAsync(ActorContext actor, long caseId, long returnId, string action, ReturnFinancialApprovalInput input, CancellationToken ct = default) {
        input = ReturnFinancialApprovalPolicy.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var frame = await PrepareAsync(actor, authority, tx, true, caseId, returnId, action, [.. input.Releases.Select(link => link.OriginalAllocationId.Value)], token);
            if (await SavedAsync(frame, token) is not null) throw B.Changed("return_financial_already_approved");
            var policy = await PolicyAsync(frame, token); var row = ReviewRow(frame, policy, input); await PeopleAsync(frame, token); await AuditReadAsync(frame, "preview", token); await FenceAsync(frame, token);
            return new ReturnFinancialApprovalPreview(new(frame.Source.Request, frame.Money.InvoiceBalance, row["receipts"].AsBsonArray, row["links"].AsBsonArray, false, null),
                A.Binding(frame.Source.Request, policy, frame.Money.InvoiceBalance, row["receipts"].AsBsonArray, row["links"].AsBsonArray, input), policy, A.Review(row));
        }, ct);
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128) || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw ReturnFinancialApprovalPolicy.Invalid();
    }
    public async Task<BsonDocument> ApproveAsync(ActorContext actor, long caseId, long returnId, string action, string binding, ReturnFinancialApprovalInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ReturnFinancialApprovalPolicy.Copy(input); Metadata(metadata);
        if (binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw ReturnFinancialApprovalPolicy.Invalid();
        await StorageAsync(ct); var body = A.Body(caseId, returnId, action, binding, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var previous = await RecoverCoreAsync(actor, caseId, returnId, action, metadata.IdempotencyKey, bodyHash, ct); if (previous is not null) return A.Result(previous);
        var id = (await ids.NextAsync(B.Events, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; Frame? frame = null;
        return await authorization.ExecuteCommandAsync(actor, A.Operation, metadata, body, async (authority, tx, token) => {
            frame = await PrepareAsync(actor, authority, tx, true, caseId, returnId, action, [.. input.Releases.Select(link => link.OriginalAllocationId.Value)], token);
            var saved = await ForKeyAsync(frame, metadata.IdempotencyKey, token); if (saved is null) await PolicyAsync(frame, token);
            var proof = new ApprovalRequest(1, 1, RequestedBy: frame.Source.Request["actorId"].AsInt64); var resources = frame.Resources;
            return new(ScopePolicy.Authorize(actor, authority, frame.Permission, resources[0], clock.UtcNow, proof),
                [.. resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct()], frame.Permission, new(resources[0], proof));
        }, async (tx, token) => {
            var ready = frame ?? throw B.Broken(); if (await SavedAsync(ready, token) is not null) throw B.Changed("return_financial_already_approved");
            var row = ReviewRow(ready, ready.Policy ?? throw B.Broken(), input);
            if (binding != A.Binding(ready.Source.Request, row["policy"].AsBsonDocument, ready.Money.InvoiceBalance, row["receipts"].AsBsonArray, row["links"].AsBsonArray, input)) throw B.Changed("return_financial_changed");
            await PeopleAsync(ready, token); await ready.Source.FenceNewAsync(token); var at = Now;
            row.AddRange(new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "operation", A.Operation }, { "binding", binding }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "recordedAt", at.UtcDateTime }, { "postingDate", InvoiceBson.Date(DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(7)).DateTime)) },
                { "people", new BsonArray(ready.People!.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId },
                { "fence", 0L }, { "postingFence", 0L }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "snapshotHash", BsonNull.Value } });
            if (row["recordedAt"].CompareTo(ready.Source.Request["requestedAt"]) < 0) throw B.Broken();
            await records.Rows(A.Events).InsertOneAsync(tx, row, cancellationToken: token);
            var journal = await new PostJournal(database, clock, [new ReturnFinancialApprovalPostingSource(database, row)]).PostAsync(tx, actor, ReturnFinancialApprovalPostingSource.Source(id), 1, new(metadata.Reason, metadata.CorrelationId), token);
            var posted = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", journal.Id)).SingleOrDefaultAsync(token) ?? throw B.Broken();
            ReturnFinancialApprovalPostingSource.RequireAccounts(row, AccountingBson.ReadDraft(posted));
            var unposted = row.DeepClone().AsBsonDocument; unposted["postingFence"] = 1L;
            row["postingFence"] = 1L; row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["snapshotHash"] = B.Hash(row);
            if ((await records.Rows(A.Events).ReplaceOneAsync(tx, unposted, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await records.SaveHeadAsync(tx, false, row["invoiceId"].AsInt64, ready.Money.InvoiceHead, row, row["invoice"]["total"], row["invoice"]["snapshotHash"], token);
            foreach (var receipt in row["receipts"].AsBsonArray) await records.SaveHeadAsync(tx, true, receipt["receiptId"].AsInt64, ready.Money.ReceiptHeads[receipt["receiptId"].AsInt64], row, receipt["receiptTotal"], receipt["receiptHash"], token);
            await records.Rows("auditEvents").InsertOneAsync(tx, A.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", A.Operation }, { "sourceKey", A.SourceKey(row) },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", A.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return A.Result(row);
        }, ct, async (authority, tx, token) => {
            var ready = frame ?? throw B.Broken(); var saved = await ForKeyAsync(ready, metadata.IdempotencyKey, token) ?? throw B.Broken();
            if (saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
            _ = await allocations.FinancialSourcesAsync(actor, authority, tx, ready.Source.Invoice.MoneySource, [.. saved["links"].AsBsonArray.Select(link => link["originalAllocationId"].AsInt64)], token);
            await PeopleAsync(ready, token); await FenceAsync(ready, token);
        }, validateCurrent: () => Current(frame ?? throw B.Broken()));
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, long caseId, long returnId, string action, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover independent financial approval", "return-financial-recovery")); await StorageAsync(ct);
        var row = await RecoverCoreAsync(actor, caseId, returnId, action, key, null, ct); return row is null ? null : A.Result(row);
    }
    private Task<BsonDocument?> RecoverCoreAsync(ActorContext actor, long caseId, long returnId, string action, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var raw = await records.Rows(A.Events).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
        var ids = raw is null ? [] : raw["links"].AsBsonArray.Select(link => link["originalAllocationId"].AsInt64).ToArray();
        var frame = await PrepareAsync(actor, authority, tx, true, caseId, returnId, action, ids, token); var saved = await ForKeyAsync(frame, key, token);
        if (saved is not null && bodyHash is not null && saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
        await PeopleAsync(frame, token); await AuditReadAsync(frame, "recover", token); await FenceAsync(frame, token); return saved;
    }, ct);
}

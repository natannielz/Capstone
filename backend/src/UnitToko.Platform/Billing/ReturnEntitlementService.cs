using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;

namespace UnitToko.Platform.Billing;
public sealed record ReturnEntitlementView(BsonDocument Source, BsonDocument Pool, BsonDocument? Request);
public sealed record ReturnEntitlementPreview(ReturnEntitlementView View, string Binding, BsonDocument Policy, ReturnEntitlementResult Result);

/// <summary>Financial maker stage. An immutable inspected request reserves original line attribution;
/// independent approval must consume this history together with canonical invoice/receipt balances.</summary>
public sealed class ReturnEntitlementService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly AfterSalesCaseService cases;
    private readonly InvoiceService invoices;
    private readonly ReturnEntitlementRecords records;
    public ReturnEntitlementService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, AfterSalesCaseService cases, InvoiceService invoices) {
        foreach (var owner in new[] { cases.FinancialSourceDatabase, invoices.PaymentSourceDatabase })
            if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw new ArgumentException("Return financial owners must share one transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.cases = cases; this.invoices = invoices; records = new(database, clock); authorization = new(database, unit, clock);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Frame(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, FinancialReturnSource returned, FinancialInvoiceSource invoice) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal FinancialReturnSource Returned = returned;
        internal FinancialInvoiceSource Invoice = invoice;
        internal BsonDocument Source = E.Source(returned, invoice);
        internal BsonDocument? Policy;
        internal PersonAuthorityProof? People;
        internal readonly List<Action> HistoricalCurrent = [];
        internal ResourceScope[] Resources => PaymentDraftBson.Scopes(Invoice.MoneySource["scopes"].AsBsonArray);
        internal string Action => Source["action"].AsString;
    }
    private AuthorizationQuery[] Require(Frame frame) {
        var permission = frame.Action == "credit" ? Permissions.CreditRequest : Permissions.RefundRequest;
        return new[] { Permissions.CaseRead, Permissions.InvoiceRead, Permissions.CaseManage, permission }.Select(name => {
            var query = ScopePolicy.Compile(frame.Actor, frame.Authority, name, clock.UtcNow); AuthorizationStore.Require(query);
            if (frame.Resources.Length == 0 || frame.Resources.Any(resource => !ScopePolicy.IsResourceActive(resource, frame.Authority.Directory) || !query.Matches(resource))) throw B.Broken(); return query;
        }).ToArray();
    }
    private void Current(Frame frame) {
        _ = Require(frame); frame.Returned.RequireCurrent(); frame.Invoice.RequireCurrent(); foreach (var check in frame.HistoricalCurrent) check();
        if (frame.Policy is { } policy) E.PolicyAt(policy, Now); frame.People?.RequireCurrent(frame.Tx, clock.UtcNow);
    }
    private async Task FenceAsync(Frame frame, bool newEffect, CancellationToken ct) {
        await (newEffect ? frame.Returned.FenceNewAsync(ct) : frame.Returned.FenceReadAsync(ct)); await frame.Invoice.FenceAsync(ct);
        var queries = Require(frame); var resources = frame.Resources; var clauses = queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches))).ToArray();
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
        await cases.FinancialStorageAsync(ct); await invoices.PaymentStorageAsync(ct); await ReturnEntitlementStorage.RequireAsync(database, ct);
    }
    private async Task<Frame> SourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long caseId, long returnId, string action, CancellationToken ct) {
        if (caseId <= 0 || returnId <= 0 || action is not ("credit" or "refund")) throw ReturnEntitlementPolicy.Invalid();
        var returned = await cases.FinancialResolutionSourceAsync(actor, authority, tx, caseId, returnId, action, ct);
        var invoice = await invoices.FinancialEntitlementSourceAsync(actor, authority, tx, returned.Root["source"]["invoiceId"].AsInt64, ct); var frame = new Frame(actor, authority, tx, returned, invoice); _ = Require(frame); return frame;
    }
    private async Task PeopleAsync(Frame frame, CancellationToken ct) {
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(frame.Tx, [.. frame.Returned.Participants.Append(frame.Actor.UserId).Distinct().Order()], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == frame.Actor.UserId), proof.Evidence.Single(person => person.UserId == frame.Source["requestedBy"].AsInt64)))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(frame.Tx, proof, ct); frame.People = proof; Current(frame);
    }
    private async Task ValidateSourceAsync(Frame frame, BsonDocument row, CancellationToken ct) {
        var returned = await cases.FinancialResolutionSourceAsync(frame.Actor, frame.Authority, frame.Tx, row["caseId"].AsInt64, row["_id"].AsInt64, row["action"].AsString, ct);
        RequireHistorySource(row, returned, frame.Invoice); frame.HistoricalCurrent.Add(returned.RequireCurrent);
        // Each historic owner also fences its captured source scopes; caller is never given producer stock grants.
        await returned.FenceReadAsync(ct);
    }
    private async Task<(BsonDocument State, BsonDocument? Head)> PoolAsync(Frame frame, CancellationToken ct) =>
        await records.PoolAsync(frame.Tx, frame.Invoice.MoneySource, frame.Source["issuedLines"].AsBsonArray, row => ValidateSourceAsync(frame, row, ct), ct);
    private async Task<BsonDocument> PolicyAsync(Frame frame, CancellationToken ct) {
        var party = PaymentDraftBson.Party(new BsonArray { frame.Invoice.MoneySource });
        var scope = new BsonDocument { { "fulfillmentOrganizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] }, { "warehouseId", BsonNull.Value } };
        var policy = await records.Rows(E.Policies).Find(frame.Tx, new BsonDocument("scope", scope) & ReturnEntitlementSchemasFilter()).SingleOrDefaultAsync(ct) ?? throw B.Changed("return_entitlement_policy_unavailable");
        E.PolicyAt(policy, Now);
        if (policy["fence"].AsInt64 >= long.MaxValue - 1) throw B.Changed("return_entitlement_policy_unavailable");
        if ((await records.Rows(E.Policies).UpdateOneAsync(frame.Tx, policy, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed("return_entitlement_policy_unavailable");
        frame.Policy = B.Snapshot(policy); return frame.Policy;
    }
    private static FilterDefinition<BsonDocument> ReturnEntitlementSchemasFilter() => UnitToko.Platform.Mongo.Migrations.ReturnEntitlementSchemas.Validator(E.Policies);
    private static void RequireHistorySource(BsonDocument row, FinancialReturnSource returned, FinancialInvoiceSource invoice) {
        ReturnEntitlementRecords.Equal(row["source"], E.Source(returned, invoice));
        if (row["requestedAt"].CompareTo(returned.Proposal["requestedAt"]) < 0) throw B.Broken();
    }
    private async Task<BsonDocument?> SavedAsync(Frame frame, CancellationToken ct) {
        var raw = await records.Rows(E.Requests).Find(frame.Tx, new BsonDocument("_id", frame.Source["returnId"])).SingleOrDefaultAsync(ct);
        if (raw is not null) { var saved = await records.RawAsync(frame.Tx, raw["_id"].AsInt64, ct); RequireHistorySource(saved, frame.Returned, frame.Invoice); return saved; }
        foreach (var (table, field, prefix) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
            if (await records.Rows(table).Find(frame.Tx, new BsonDocument { { field, E.Operation }, { prefix + ".returnId", frame.Source["returnId"] } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    private async Task AuditReadAsync(Frame frame, string action, CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(frame.Tx, new BsonDocument {
        { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", "billing.return.entitlement.read" }, { "operation", "billing.return.entitlement." + action },
        { "actorId", frame.Actor.UserId }, { "actorGrantVersion", frame.Actor.GrantVersion }, { "returnId", frame.Source["returnId"] }, { "createdAt", Now.UtcDateTime }
    }, cancellationToken: ct);
    public async Task<ReturnEntitlementView> ReadAsync(ActorContext actor, long caseId, long returnId, string action, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var frame = await SourceAsync(actor, authority, tx, caseId, returnId, action, token); var pool = await PoolAsync(frame, token); var saved = await SavedAsync(frame, token);
            await PeopleAsync(frame, token); await AuditReadAsync(frame, "query", token); await FenceAsync(frame, false, token); return new ReturnEntitlementView(frame.Source, pool.State, saved is null ? null : E.Result(saved));
        }, ct);
    }
    public async Task<ReturnEntitlementPreview> PreviewAsync(ActorContext actor, long caseId, long returnId, string action, ReturnEntitlementInput input, CancellationToken ct = default) {
        input = ReturnEntitlementPolicy.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var frame = await SourceAsync(actor, authority, tx, caseId, returnId, action, token); var pool = await PoolAsync(frame, token);
            if (await SavedAsync(frame, token) is not null) throw B.Changed("return_entitlement_already_requested");
            var policy = await PolicyAsync(frame, token); var result = E.Review(frame.Source, pool.State, policy, input); await PeopleAsync(frame, token);
            await AuditReadAsync(frame, "preview", token); await FenceAsync(frame, false, token);
            return new ReturnEntitlementPreview(new(frame.Source, pool.State, null), E.Binding(frame.Source, pool.State, policy), policy, result);
        }, ct);
    }
    public async Task<BsonDocument> RequestAsync(ActorContext actor, long caseId, long returnId, string action, long version, string binding, ReturnEntitlementInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ReturnEntitlementPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (version is < 0 or >= long.MaxValue - 1 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch)) ||
            !ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128) || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw ReturnEntitlementPolicy.Invalid();
        await StorageAsync(ct); var body = E.Body(caseId, returnId, action, version, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value; Frame? frame = null;
        return await authorization.ExecuteCommandAsync(actor, E.Operation, metadata, body, async (authority, tx, token) => {
            frame = await SourceAsync(actor, authority, tx, caseId, returnId, action, token); var resources = frame.Resources; var approval = new ApprovalRequest(1, 1, RequestedBy: frame.Source["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var ready = frame ?? throw B.Broken(); var pool = await PoolAsync(ready, token);
            if (pool.State["version"] != version || await SavedAsync(ready, token) is not null) throw B.Changed("return_entitlement_changed");
            var policy = await PolicyAsync(ready, token); var result = E.Review(ready.Source, pool.State, policy, input);
            if (binding != E.Binding(ready.Source, pool.State, policy)) throw B.Changed("return_entitlement_changed"); await PeopleAsync(ready, token);
            var row = new BsonDocument { { "_id", returnId }, { "schemaVersion", 1 }, { "invoiceId", ready.Source["invoiceId"] }, { "caseId", caseId }, { "action", action },
                { "source", ready.Source }, { "policy", policy }, { "before", pool.State }, { "version", version + 1 }, { "binding", binding }, { "input", E.Input(input) }, { "lines", E.Lines(result) },
                { "net", InvoiceBson.Money(result.Net) }, { "tax", InvoiceBson.Money(result.Tax) }, { "charges", InvoiceBson.Money(result.Charges) }, { "credit", InvoiceBson.Money(result.Credit) },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "requestedAt", Now.UtcDateTime }, { "people", new BsonArray(ready.People!.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            RequireHistorySource(row, ready.Returned, ready.Invoice); row.Add("snapshotHash", B.Hash(row)); await ready.Returned.FenceNewAsync(token); await records.RecordAsync(tx, row, pool.Head, token); return E.Result(row);
        }, ct, async (authority, tx, token) => {
            var ready = frame ?? throw B.Broken(); var saved = await SavedAsync(ready, token) ?? throw B.Broken();
            if (saved["actorId"] != actor.UserId || saved["requestKey"] != metadata.IdempotencyKey || saved["bodyHash"] != hash) throw B.Broken();
            _ = await PoolAsync(ready, token); await PeopleAsync(ready, token); await FenceAsync(ready, false, token);
        }, validateCurrent: () => Current(frame ?? throw B.Broken()));
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, long caseId, long returnId, string action, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original financial request", "return-entitlement-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var frame = await SourceAsync(actor, authority, tx, caseId, returnId, action, token); _ = await PoolAsync(frame, token); var saved = await records.ForKeyAsync(tx, actor.UserId, key, token);
            if (saved is not null) { ReturnEntitlementRecords.Equal(saved["source"], frame.Source); if (saved["_id"] != returnId || saved["caseId"] != caseId || saved["action"] != action) throw B.Broken(); }
            await PeopleAsync(frame, token); await AuditReadAsync(frame, "recover", token); await FenceAsync(frame, false, token); return saved is null ? null : E.Result(saved);
        }, ct);
    }
}

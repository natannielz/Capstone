using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

/// <summary>Records quarantined physical observations and mandatory original unboxing evidence; never writes stock or money.</summary>
public sealed partial class ProcurementInspectionService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly ProcurementInspectionRecords records;
    private readonly ProcurementReceivingService receiving;
    private readonly PurchaseOrderService orders;
    public ProcurementInspectionService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, ProcurementReceivingService receiving) {
        this.database = database; this.ids = ids; this.clock = clock; this.receiving = receiving; this.orders = orders;
        authorization = new(database, unit, clock); records = new(database, unit, clock, catalog, orders);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        internal readonly ActorContext Actor = actor;
        internal readonly AuthorizationSnapshot Authority = authority;
        internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly ProcurementLocation Location = location;
        internal readonly string Permission = permission;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Location.OrganizationId, ShopId: Location.ShopId, WarehouseId: Location.WarehouseId, ChannelId: Location.ChannelId);
        internal ProcurementInspectionHistory? History;
        internal BsonDocument? Draft, Submission;
        internal string? SourceHash;
        internal PersonAuthorityProof? People;
        internal DateTimeOffset? SessionExpiry;
        internal bool IncludeEvidence;
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context) => new[] { Permissions.ProcurementRead, context.Permission }.Concat(context.IncludeEvidence ? new[] { Permissions.ProcurementInspectionEvidenceRead } : Array.Empty<string>()).Distinct().Select(permission => {
        var query = Query(context.Actor, context.Authority, permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
    }).ToArray();
    private Context ContextFor(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        ProcurementRequestRules.Location(location); var result = new Context(actor, authority, tx, location, permission); _ = Require(result); return result;
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) || context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction ||
            context.History is { } history && context.SourceHash != B.Hash(history.Source)) throw B.Broken();
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    internal async Task StorageAsync(CancellationToken ct) {
        await orders.RequireReceivingStorageAsync(ct);
        foreach (var (names, validator, keys) in new[] {
            (ProcurementReceivingSchemas.Collections, (Func<string, BsonDocument>)ProcurementReceivingSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementReceivingSchemas.Keys),
            (ProcurementInspectionSchemas.Collections, (Func<string, BsonDocument>)ProcurementInspectionSchemas.Validator, (Func<string, BsonDocument[]>)ProcurementInspectionSchemas.Keys) })
        foreach (var name in names) {
            await InventoryRepository.RequireStorageAsync(database, name, validator(name), keys(name), ct);
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
    }
    private async Task PeopleAsync(Context context, BsonDocument? original, CancellationToken ct) {
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [context.Actor.UserId], ct);
        var person = proof.Evidence.SingleOrDefault();
        if (person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 } || person.UserId != context.Actor.UserId) throw new AuthorizationDeniedException("person_evidence_invalid");
        if (original is not null && original["actorId"] == context.Actor.UserId && R.Person(original["people"][0]).PersonId != person.PersonId) throw new AuthorizationDeniedException("person_evidence_invalid");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private async Task<Context> IntakeContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long noticeId, long intakeId, string permission, CancellationToken ct) {
        var route = await Rows(C.Intakes).Find(tx, new BsonDocument { { "_id", intakeId }, { "noticeId", noticeId } }).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = ContextFor(actor, authority, tx, R.Location(route["location"]), permission);
        context.History = await records.HistoryAsync(tx, noticeId, intakeId, ct); context.SourceHash = B.Hash(context.History.Source);
        await PeopleAsync(context, null, ct); Current(context); return context;
    }
    private async Task<Context> DraftContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, bool requireDraft, CancellationToken ct) {
        var route = await Rows(B.Drafts).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument { { "noticeId", 1 }, { "intakeId", 1 }, { "location", 1 } }).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        _ = ContextFor(actor, authority, tx, R.Location(route["location"]), permission);
        var context = await IntakeContextAsync(actor, authority, tx, route["noticeId"].AsInt64, route["intakeId"].AsInt64, permission, ct);
        context.Draft = context.History!.Drafts.SingleOrDefault(row => row["_id"] == id) ?? throw B.Broken();
        context.Submission = context.History.Submissions.SingleOrDefault(row => row["draftId"] == id);
        if (permission == Permissions.ProcurementInspect && context.Draft["actorId"] != actor.UserId) throw new AuthorizationDeniedException("scope_denied");
        if (requireDraft && context.Submission is not null) throw B.Changed(); await PeopleAsync(context, context.Draft, ct); return context;
    }
    private async Task TouchAsync(Context context, string table, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 2 || (await Rows(table).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context);
        if (context.History is { } history) {
            await TouchAsync(context, C.Heads, history.CustodyHead, ct);
            if (history.Head is { } head) await TouchAsync(context, B.Heads, head, ct);
        }
        if (context.Draft is { } draft) await TouchAsync(context, B.Drafts, draft, ct);
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var clauses = new[] { Permissions.ProcurementRead, permission }.Distinct().SelectMany(value => Query(actor, authority, value).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct); foreach (var value in new[] { Permissions.ProcurementRead, permission }.Distinct()) _ = Query(actor, authority, value);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context) => new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, context.Resource, clock.UtcNow),
        ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), context.Permission, new(context.Resource));
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); ProcurementInspectionRules.Text(metadata.Reason, 1000); ProcurementInspectionRules.Text(metadata.CorrelationId, 128); Key(metadata.IdempotencyKey);
    }
    private static void Key(string value) { ProcurementInspectionRules.Text(value, 128); if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw ProcurementInspectionRules.Invalid(); }
    private static void Binding(string value) { if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementInspectionRules.Invalid(); }
    private static void Revision(long id, long version, long reservation) { if (id <= 0 || version != 1 || reservation <= 0) throw ProcurementInspectionRules.Invalid(); }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", row["operation"] },
            { "sourceKey", B.EffectKey(row) }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}

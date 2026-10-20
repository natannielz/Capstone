using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Catalog.CatalogPublicShowcaseBson;

namespace UnitToko.Platform.Catalog;

/// <summary>Which assortments the signed-out public catalog shows. A maker proposes making an assortment public or private and an
/// independent checker approves; nothing is public before that. Prices never take part: this decides visibility of product facts only.</summary>
public sealed class CatalogPublicShowcaseService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    public CatalogPublicShowcaseService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    {
        this.database = database; this.ids = ids; this.clock = clock; authorization = new(database, unit, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, string permission, BsonDocument assortment, GrantScope owner)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal BsonDocument Assortment { get; } = assortment;
        internal GrantScope Owner { get; } = owner;
        internal ResourceScope Resource => CatalogRepository.Resource(Owner);
        internal BsonDocument? Head { get; set; }
    }

    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!CatalogReceiptRules.Key(metadata.IdempotencyKey) || !CatalogPriceBson.Text(metadata.Reason, 1000) || !CatalogPriceBson.Text(metadata.CorrelationId, 128))
            throw B.Invalid("invalid_catalog_public_request");
    }

    private AuthorizationQuery Require(Context context)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, context.Permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (!query.Matches(context.Resource)) throw B.Invalid("scope_denied");
        return query;
    }

    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long assortmentId,
        string permission, CancellationToken ct)
    {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow));
        var row = await Rows("catalogAssortments").Find(tx, F.Eq("_id", assortmentId)).SingleOrDefaultAsync(ct) ?? throw B.Invalid("resource_unavailable");
        var assortment = CatalogRepository.Assortment(row);
        var context = new Context(actor, snapshot, tx, permission, assortment, CatalogRepository.Owner(assortment["ownerScope"].AsBsonDocument));
        _ = Require(context);
        context.Head = await Rows(B.Heads).Find(tx, F.Eq("_id", assortmentId)).SingleOrDefaultAsync(ct);
        return context;
    }

    private async Task<BsonDocument?> PendingAsync(IClientSessionHandle tx, long assortmentId, CancellationToken ct)
    {
        var proposals = await Rows(B.Proposals).Find(tx, F.Eq("assortmentId", assortmentId)).Sort(new BsonDocument("_id", -1)).Limit(20).ToListAsync(ct);
        foreach (var proposal in proposals) {
            B.Require(proposal);
            if (!await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).AnyAsync(ct)) return proposal;
        }
        return null;
    }

    /// <summary>Assortments this person may propose for or review, with their public state and any pending proposal.</summary>
    public async Task<ImmutableArray<CatalogPublicShowcase>> ListAsync(ActorContext actor, CancellationToken ct = default)
    {
        await B.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var propose = ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicPropose, clock.UtcNow);
            var approve = ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicApprove, clock.UtcNow);
            if (!propose.IsAllowed && !approve.IsAllowed) throw new AuthorizationDeniedException(propose.DenialCode ?? "permission_denied");
            var result = ImmutableArray.CreateBuilder<CatalogPublicShowcase>();
            foreach (var raw in await Rows("catalogAssortments").Find(tx, F.Eq("status", "active")).Sort(new BsonDocument("_id", 1)).Limit(200).ToListAsync(token)) {
                var assortment = CatalogRepository.Assortment(raw); var owner = CatalogRepository.Owner(assortment["ownerScope"].AsBsonDocument);
                var resource = CatalogRepository.Resource(owner);
                if (!propose.Matches(resource) && !approve.Matches(resource)) continue;
                var head = await Rows(B.Heads).Find(tx, F.Eq("_id", assortment["_id"])).SingleOrDefaultAsync(token);
                var pending = await PendingAsync(tx, assortment["_id"].AsInt64, token);
                var listed = await Rows("skus").CountDocumentsAsync(tx, F.Eq("active", true) & F.ElemMatch<BsonDocument>("catalogListings", F.Eq("assortmentId", assortment["_id"])), cancellationToken: token);
                result.Add(new(assortment["_id"].AsInt64, owner, null, listed, head?["isPublic"].AsBoolean ?? false, head?["revision"].AsInt64 ?? 0,
                    pending is null ? null : new(pending["_id"].AsInt64, pending["makePublic"].AsBoolean, pending["proposedBy"].AsInt64,
                        CatalogRepository.Date(pending["proposedAt"]), pending["reason"].AsString)));
            }
            // Staff recognise a catalog by its shop and warehouse names; an unnamed or malformed profile leaves the name out instead of guessing.
            var ids = result.SelectMany(item => new[] { item.Owner.OrganizationId, item.Owner.ShopId, item.Owner.WarehouseId }).OfType<long>().Distinct().ToArray();
            var names = new Dictionary<long, string>();
            foreach (var profile in await Rows("organizationProfiles").Find(tx, F.In("_id", ids)).ToListAsync(token))
                if (profile.TryGetValue("_id", out var key) && key.IsInt64 && profile.TryGetValue("name", out var name) && name.IsString &&
                    name.AsString.Trim() is { Length: > 0 and <= 200 } text && !text.Any(char.IsControl)) names[key.AsInt64] = text;
            string? Named(long? id) => id is { } value && names.TryGetValue(value, out var text) ? text : null;
            string? Label(GrantScope owner)
            {
                if (owner.ShopId is null) return Named(owner.OrganizationId);
                var shop = Named(owner.ShopId);
                if (shop is null || owner.WarehouseId is null) return shop;
                return Named(owner.WarehouseId) is { } warehouse ? shop + " · " + warehouse : null;
            }
            return result.Select(item => item with { OwnerName = Label(item.Owner) }).ToImmutableArray();
        }, ct);
    }

    public async Task<BsonDocument> ProposeAsync(ActorContext actor, long assortmentId, long expectedRevision, bool makePublic,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (assortmentId <= 0 || expectedRevision is < 0 or >= long.MaxValue - 2) throw B.Invalid("invalid_catalog_public_request");
        await B.RequireStorageAsync(database, ct);
        var body = new { assortmentId, expectedRevision, makePublic }; var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await CommandAsync(actor, B.Prefix + "propose", Permissions.CatalogPublicPropose, metadata, body,
            async (snapshot, tx, token) => (await ContextAsync(actor, snapshot, tx, assortmentId, Permissions.CatalogPublicPropose, token), null),
            async (context, token) => {
                var revision = context.Head?["revision"].AsInt64 ?? 0; var isPublic = context.Head?["isPublic"].AsBoolean ?? false;
                if (context.Assortment["status"] != "active" || revision != expectedRevision) throw B.Changed();
                if (isPublic == makePublic) throw new PersistenceConflictException("catalog_public_showcase_unchanged", "The assortment already has this public state");
                if (await PendingAsync(context.Tx, assortmentId, token) is not null)
                    throw new PersistenceConflictException("catalog_public_showcase_pending", "A proposal for this assortment is already waiting for review");
                var now = Now();
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "assortmentId", assortmentId }, { "makePublic", makePublic },
                    { "baseRevision", revision }, { "revision", 1L }, { "proposedBy", actor.UserId }, { "proposedAt", now.UtcDateTime } };
                B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, hash);
                await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await EffectsAsync(context.Tx, row, token);
                return B.Result(row);
            }, ct);
    }

    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, CatalogPublicShowcaseDecision decision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw B.Invalid("invalid_catalog_public_request");
        await B.RequireStorageAsync(database, ct);
        var approve = decision == CatalogPublicShowcaseDecision.Approve; var operation = B.Prefix + (approve ? "approve" : "reject");
        var body = new { proposalId, expectedRevision, decision = approve ? "approve" : "reject" }; var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        BsonDocument? selected = null;
        return await CommandAsync(actor, operation, Permissions.CatalogPublicApprove, metadata, body,
            async (snapshot, tx, token) => {
                var proposal = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId)).SingleOrDefaultAsync(token) ?? throw B.Invalid("resource_unavailable");
                B.Require(proposal); selected = proposal;
                var context = await ContextAsync(actor, snapshot, tx, proposal["assortmentId"].AsInt64, Permissions.CatalogPublicApprove, token);
                return (context, new ApprovalRequest(proposal["revision"].AsInt64, expectedRevision, ProposedBy: proposal["proposedBy"].AsInt64));
            },
            async (context, token) => {
                var proposal = selected ?? throw B.Broken();
                if (proposal["revision"].AsInt64 != expectedRevision) throw B.Changed();
                if (await Rows(B.Decisions).Find(context.Tx, F.Eq("proposalId", proposalId)).AnyAsync(token))
                    throw new PersistenceConflictException("catalog_public_showcase_decided", "This proposal already has a decision");
                var now = Now(); if (now < CatalogRepository.Date(proposal["proposedAt"])) throw B.Broken();
                BsonValue headRevision = BsonNull.Value;
                if (approve) {
                    var current = context.Head?["revision"].AsInt64 ?? 0;
                    if (context.Assortment["status"] != "active" || current != proposal["baseRevision"].AsInt64) throw B.Changed();
                    headRevision = current + 1;
                    var head = new BsonDocument { { "_id", proposal["assortmentId"] }, { "schemaVersion", 1 }, { "isPublic", proposal["makePublic"] },
                        { "revision", headRevision }, { "decisionId", id }, { "updatedAt", now.UtcDateTime },
                        { "fence", context.Head is null ? 0L : context.Head["fence"].AsInt64 + 1 } };
                    if (context.Head is null) await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: token);
                    else if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, context.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                    context.Head = head;
                }
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "assortmentId", proposal["assortmentId"] },
                    { "decision", approve ? "approve" : "reject" }, { "headRevision", headRevision }, { "decidedBy", actor.UserId }, { "decidedAt", now.UtcDateTime } };
                B.Metadata(row, operation, auditId, actor, metadata, hash);
                await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await EffectsAsync(context.Tx, row, token);
                return B.Result(row);
            }, ct);
    }

    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.Prefix + "command:" + row["auditId"].AsInt64 },
            { "kind", B.Prefix + "changed" }, { "createdAt", B.At(row) }, { "status", "pending" }, { "attempts", 0 },
            { "dueAt", B.At(row) }, { "payload", B.Result(row) } }, cancellationToken: ct);
    }

    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, string permission, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<(Context Context, ApprovalRequest? Approval)>> target,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct)
    {
        Context? captured = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            var result = await target(snapshot, tx, token); captured = result.Context;
            var query = Require(captured);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(captured.Resource)).Select(clause => clause.GrantId).ToImmutableArray()),
                ScopePolicy.RequiredNodes(captured.Resource, snapshot.Directory), permission, new(captured.Resource, result.Approval));
        }, (_, token) => write(captured ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = captured ?? throw B.Broken();
            // A replay returns the original result only while its exact record still stands and the caller still holds authority.
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) &
                F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(token) ?? throw B.Broken();
            var result = receipt["result"].AsBsonDocument;
            var record = operation == B.Prefix + "propose"
                ? await Rows(B.Proposals).Find(tx, F.Eq("_id", result["proposalId"])).SingleOrDefaultAsync(token)
                : await Rows(B.Decisions).Find(tx, F.Eq("_id", result["decisionId"])).SingleOrDefaultAsync(token);
            if (record is null) throw B.Broken();
            B.Require(record);
            if (record["actorId"].AsInt64 != actor.UserId || record["operation"] != operation || record["requestKey"] != metadata.IdempotencyKey ||
                record["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) || !result.Equals(B.Result(record))) throw B.Broken();
            await CompleteAsync(context, token);
        });
    }

    private async Task CompleteAsync(Context context, CancellationToken ct)
    {
        var clauses = Require(context).Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(context.Resource, context.Snapshot.Directory)).Distinct(),
            [], context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.Actor.Session is { } reference) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(reference, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).AnyAsync(ct);
            if (!live) throw B.Invalid("stale_or_inactive_session");
        }
        _ = Require(context); ct.ThrowIfCancellationRequested();
    }
}

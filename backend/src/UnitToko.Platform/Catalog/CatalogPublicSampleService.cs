using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using S = UnitToko.Platform.Mongo.Migrations.CatalogPublicSampleSchemas;

namespace UnitToko.Platform.Catalog;

/// <summary>Shared rules for the guest sample: storage checks, record integrity and what "published now" means.</summary>
internal static class CatalogPublicSampleBson
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    internal static AuthorizationDeniedException Invalid(string code = "catalog_public_sample_unavailable") => new(code);
    internal static PersistenceConflictException Changed() =>
        new("catalog_public_sample_changed", "The guest sample changed; reload it and review again");
    internal static PersistenceConflictException Broken() =>
        new("catalog_public_sample_integrity_unavailable", "The guest sample history is unavailable");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(item => item.Name != "snapshotHash")));
    internal static BsonDocument Require(BsonDocument row) => row.GetValue("snapshotHash", BsonNull.Value) == Hash(row) ? row : throw Broken();

    internal static async Task RequireStorageAsync(IMongoDatabase db, CancellationToken ct) {
        foreach (var name in S.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(db, name, ct) ?? throw Invalid("public_catalog_unavailable");
            var keys = S.Keys(name).Select((key, index) => ("ux_" + name + "_" + index, key, true)).ToArray();
            var indexes = await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!AccountContactStorage.SafeDefinition(definition, S.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                throw Invalid("public_catalog_unavailable");
        }
    }

    /// <summary>Packages published now: listed in an active assortment of the same organisation and owner. Null when none can be.</summary>
    internal static async Task<FilterDefinition<BsonDocument>?> PublishedAsync(IMongoDatabase db, IClientSessionHandle? tx, IEnumerable<long> organizations,
        CancellationToken ct) {
        var rows = db.GetCollection<BsonDocument>("catalogAssortments");
        var filter = F.Eq("status", "active") & F.In("ownerScope.organizationId", organizations.Distinct());
        var assortments = tx is null ? await rows.Find(filter).Limit(1001).ToListAsync(ct) : await rows.Find(tx, filter).Limit(1001).ToListAsync(ct);
        if (assortments.Count > 1000) throw Invalid("public_catalog_limit");
        if (assortments.Count == 0) return null;
        return F.And(F.Eq("active", true), F.Or(assortments.Select(CatalogRepository.Assortment).Select(row => {
            var owner = CatalogRepository.Owner(row["ownerScope"].AsBsonDocument);
            return F.And(F.Eq("organizationId", owner.OrganizationId!.Value), F.ElemMatch<BsonDocument>("catalogListings",
                F.And(F.Eq("assortmentId", row["_id"]), UnitToko.Platform.Authorization.CatalogVisibilityQuery.Owner("ownerScope", owner))));
        })));
    }
}

/// <summary>Which products (catalog families) guests see as samples. A maker proposes adding and removing products in one
/// proposal; an independent checker approves or rejects it. Nothing changes for guests before approval, and prices never take part.</summary>
public sealed class CatalogPublicSampleService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    public CatalogPublicSampleService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    {
        this.database = database; this.ids = ids; this.clock = clock; authorization = new(database, unit, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, string permission, GrantScope owner)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal GrantScope Owner { get; } = owner;
        internal ResourceScope Resource => CatalogRepository.Resource(Owner);
    }

    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!CatalogReceiptRules.Key(metadata.IdempotencyKey) || !CatalogPriceBson.Text(metadata.Reason, 1000) || !CatalogPriceBson.Text(metadata.CorrelationId, 128))
            throw new DomainValidationException("invalid_catalog_public_sample_request", "Complete the guest sample request");
    }

    private AuthorizationQuery Require(Context context)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, context.Permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (!query.Matches(context.Resource)) throw CatalogPublicSampleBson.Invalid("scope_denied");
        return query;
    }

    /// <summary>Products this person may propose for or review: current samples first, then by name. Also the proposals waiting in scope.</summary>
    public async Task<CatalogPublicSamplePage> QueryAsync(ActorContext actor, string? search, int offset, int limit, CancellationToken ct = default)
    {
        if (offset is < 0 or > 5000 || limit is < 1 or > 50 || search is { Length: > 64 })
            throw new DomainValidationException("invalid_catalog_public_sample_request", "Use a bounded guest sample search");
        await CatalogPublicSampleBson.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var propose = ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicPropose, clock.UtcNow);
            var approve = ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicApprove, clock.UtcNow);
            if (!propose.IsAllowed && !approve.IsAllowed) throw new AuthorizationDeniedException(propose.DenialCode ?? "permission_denied");
            bool InScope(GrantScope owner) => propose.Matches(CatalogRepository.Resource(owner)) || approve.Matches(CatalogRepository.Resource(owner));
            var samples = (await Rows(S.Samples).Find(tx, F.Empty).Limit(5001).ToListAsync(token)).Select(row => row["_id"].AsInt64).ToHashSet();
            var families = await Rows("productFamilies").Find(tx, F.Eq("active", true) | F.In("_id", samples))
                .Project(new BsonDocument { { "_id", 1 }, { "name", 1 }, { "authoringOwner", 1 }, { "organizationId", 1 } }).Limit(5001).ToListAsync(token);
            if (families.Count > 5000 || samples.Count > 5000) throw CatalogPublicSampleBson.Invalid("catalog_public_sample_query_limit");
            var names = families.ToDictionary(row => row["_id"].AsInt64, row => row["name"].AsString);
            var owners = families.ToDictionary(row => row["_id"].AsInt64, row => CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument));
            var terms = (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(term => term.ToLowerInvariant()).ToArray();
            var matching = families.Where(row => InScope(owners[row["_id"].AsInt64]) &&
                    terms.All(term => row["name"].AsString.ToLowerInvariant().Contains(term, StringComparison.Ordinal)))
                .OrderByDescending(row => samples.Contains(row["_id"].AsInt64)).ThenBy(row => row["name"].AsString, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row["_id"].AsInt64).ToArray();
            var page = matching.Skip(offset).Take(limit).ToArray();
            var pageIds = page.Select(row => row["_id"].AsInt64).ToArray();
            var packages = new Dictionary<long, long>();
            var published = pageIds.Length == 0 ? null
                : await CatalogPublicSampleBson.PublishedAsync(database, tx, page.Select(row => row["organizationId"].AsInt64), token);
            if (published is not null)
                foreach (var row in await Rows("skus").Aggregate(tx).Match(published & F.In("familyId", pageIds))
                    .Group(new BsonDocument { { "_id", "$familyId" }, { "count", new BsonDocument("$sum", 1) } }).ToListAsync(token))
                    packages[row["_id"].AsInt64] = row["count"].ToInt64();
            var pending = await Rows(S.Proposals).Find(tx, F.Eq("state", "pending")).Sort(new BsonDocument("_id", 1)).Limit(1001).ToListAsync(token);
            if (pending.Count > 1000) throw CatalogPublicSampleBson.Invalid("catalog_public_sample_query_limit");
            var waiting = new Dictionary<long, CatalogPublicSamplePending>();
            var proposals = new List<BsonDocument>();
            foreach (var proposal in pending.Select(CatalogPublicSampleBson.Require)) {
                var owner = CatalogRepository.Owner(proposal["ownerScope"].AsBsonDocument);
                if (!InScope(owner)) continue;
                var own = proposal["proposedBy"].AsInt64 == actor.UserId;
                foreach (var id in proposal["adds"].AsBsonArray) waiting[id.AsInt64] = new(proposal["_id"].AsInt64, true, own);
                foreach (var id in proposal["removes"].AsBsonArray) waiting[id.AsInt64] = new(proposal["_id"].AsInt64, false, own);
                proposals.Add(proposal);
            }
            var label = await LabelsAsync(tx, page.Select(row => owners[row["_id"].AsInt64]).Concat(proposals.Select(row => CatalogRepository.Owner(row["ownerScope"].AsBsonDocument))), token);
            CatalogPublicSampleChange Change(BsonValue id) => new(id.AsInt64, names.TryGetValue(id.AsInt64, out var name) ? name : "Produk " + id.AsInt64.ToString(CultureInfo.InvariantCulture));
            var items = page.Select(row => {
                var id = row["_id"].AsInt64;
                return new CatalogPublicSampleItem(id, row["name"].AsString, owners[id], label(owners[id]), packages.GetValueOrDefault(id), samples.Contains(id), waiting.GetValueOrDefault(id));
            }).ToImmutableArray();
            var reviews = proposals.Take(50).Select(row => new CatalogPublicSampleProposal(row["_id"].AsInt64, row["proposedBy"].AsInt64 == actor.UserId,
                label(CatalogRepository.Owner(row["ownerScope"].AsBsonDocument)), row["reason"].AsString, CatalogRepository.Date(row["proposedAt"]),
                [.. row["adds"].AsBsonArray.Select(Change)], [.. row["removes"].AsBsonArray.Select(Change)])).ToImmutableArray();
            return new CatalogPublicSamplePage(matching.Length, items, reviews, samples.Count, S.Limit);
        }, ct);
    }

    /// <summary>Staff recognise a catalog by its shop and warehouse names; an unnamed or malformed profile falls back to its number.</summary>
    private async Task<Func<GrantScope, string>> LabelsAsync(IClientSessionHandle tx, IEnumerable<GrantScope> scopes, CancellationToken ct)
    {
        var list = scopes.ToArray();
        var keys = list.SelectMany(item => new[] { item.OrganizationId, item.ShopId, item.WarehouseId }).OfType<long>().Distinct().ToArray();
        var names = new Dictionary<long, string>();
        foreach (var profile in await Rows("organizationProfiles").Find(tx, F.In("_id", keys)).ToListAsync(ct))
            if (profile.TryGetValue("_id", out var key) && key.IsInt64 && profile.TryGetValue("name", out var name) && name.IsString &&
                name.AsString.Trim() is { Length: > 0 and <= 200 } text && !text.Any(char.IsControl)) names[key.AsInt64] = text;
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        return owner => {
            if (owner.ShopId is { } shop && names.TryGetValue(shop, out var shopName))
                return owner.WarehouseId is { } warehouse ? shopName + " · " + (names.TryGetValue(warehouse, out var warehouseName) ? warehouseName : "gudang " + Number(warehouse)) : shopName;
            var fallback = owner.ShopId is { } id ? "Toko " + Number(id) : "Organisasi " + Number(owner.OrganizationId ?? 0);
            return fallback + (owner.WarehouseId is { } store ? " · gudang " + Number(store) : "") + " (nama belum tersedia)";
        };
    }

    public async Task<BsonDocument> ProposeAsync(ActorContext actor, ImmutableArray<long> adds, ImmutableArray<long> removes,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (adds.IsDefault || removes.IsDefault || adds.Length > S.ChangeLimit || removes.Length > S.ChangeLimit || adds.Length + removes.Length == 0 ||
            adds.Concat(removes).Any(id => id <= 0) || adds.Concat(removes).Distinct().Count() != adds.Length + removes.Length)
            throw new DomainValidationException("invalid_catalog_public_sample_request", "Choose distinct products to add or remove");
        await CatalogPublicSampleBson.RequireStorageAsync(database, ct);
        var added = adds.Order().ToArray(); var removed = removes.Order().ToArray(); var all = added.Concat(removed).ToArray();
        var body = new { adds = added, removes = removed }; var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var id = (await ids.NextAsync(S.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var operation = S.Prefix + "propose";
        return await CommandAsync(actor, operation, Permissions.CatalogPublicPropose, metadata, body,
            async (snapshot, tx, token) => {
                AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicPropose, clock.UtcNow));
                // A product's owner comes from its family; a sample whose family row is gone keeps the owner it was approved under.
                var families = (await Rows("productFamilies").Find(tx, F.In("_id", all)).ToListAsync(token))
                    .ToDictionary(row => row["_id"].AsInt64, row => CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument));
                var current = (await Rows(S.Samples).Find(tx, F.In("_id", removed)).ToListAsync(token))
                    .ToDictionary(row => row["_id"].AsInt64, row => CatalogRepository.Owner(row["ownerScope"].AsBsonDocument));
                if (all.Any(familyId => !families.ContainsKey(familyId) && !current.ContainsKey(familyId)))
                    throw CatalogPublicSampleBson.Invalid("resource_unavailable");
                var owners = all.Select(familyId => families.TryGetValue(familyId, out var owner) ? owner : current[familyId]).Distinct().ToArray();
                if (owners.Length != 1)
                    throw new DomainValidationException("catalog_public_sample_owner_mixed", "Propose the products of one catalog owner at a time");
                var context = new Context(actor, snapshot, tx, Permissions.CatalogPublicPropose, owners[0]);
                _ = Require(context);
                return (context, null);
            },
            async (context, token) => {
                await RequireChangeAsync(context.Tx, added, removed, true, token);
                var now = Now();
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "ownerScope", CatalogRepository.Owner(context.Owner) },
                    { "adds", new BsonArray(added) }, { "removes", new BsonArray(removed) }, { "families", new BsonArray(all) },
                    { "proposedBy", actor.UserId }, { "proposedAt", now.UtcDateTime }, { "state", "pending" }, { "revision", 1L }, { "decision", BsonNull.Value },
                    { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                    { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash }, { "reason", metadata.Reason },
                    { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
                row.Add("snapshotHash", CatalogPublicSampleBson.Hash(row));
                await Rows(S.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token);
                var result = new BsonDocument { { "proposalId", id }, { "state", "pending" } };
                await EffectsAsync(context.Tx, auditId, operation, actor, now, row, result, token);
                return result;
            }, (record, actorId) => record["actorId"].AsInt64 == actorId && record["requestKey"] == metadata.IdempotencyKey, ct);
    }

    /// <summary>The proposed change still applies: additions are active and not yet samples, removals are samples, no product waits
    /// in another proposal, and the set does not grow past the limit.</summary>
    private async Task RequireChangeAsync(IClientSessionHandle tx, long[] adds, long[] removes, bool proposing, CancellationToken ct)
    {
        var all = adds.Concat(removes).ToArray();
        var present = (await Rows(S.Samples).Find(tx, F.In("_id", all)).ToListAsync(ct)).Select(row => row["_id"].AsInt64).ToHashSet();
        var active = (await Rows("productFamilies").Find(tx, F.In("_id", adds) & F.Eq("active", true)).Project(new BsonDocument("_id", 1)).ToListAsync(ct))
            .Select(row => row["_id"].AsInt64).ToHashSet();
        if (adds.Any(present.Contains) || removes.Any(id => !present.Contains(id)) || adds.Any(id => !active.Contains(id)))
            throw proposing ? new PersistenceConflictException("catalog_public_sample_unchanged", "A product already has the proposed guest state") : CatalogPublicSampleBson.Changed();
        if (proposing && await Rows(S.Proposals).Find(tx, F.Eq("state", "pending") & F.In("families", all)).AnyAsync(ct))
            throw new PersistenceConflictException("catalog_public_sample_pending", "A product in this change is already waiting for review");
        var count = await Rows(S.Samples).CountDocumentsAsync(tx, F.Empty, cancellationToken: ct);
        if (adds.Length > removes.Length && count + adds.Length - removes.Length > S.Limit)
            throw new PersistenceConflictException("public_sample_limit", "Guests can see at most 200 sample products; remove some first");
    }

    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, CatalogPublicSampleDecision decision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata);
        if (proposalId <= 0 || !Enum.IsDefined(decision)) throw new DomainValidationException("invalid_catalog_public_sample_request", "Choose a waiting proposal");
        await CatalogPublicSampleBson.RequireStorageAsync(database, ct);
        var approve = decision == CatalogPublicSampleDecision.Approve; var operation = S.Prefix + (approve ? "approve" : "reject");
        var body = new { proposalId, decision = approve ? "approve" : "reject" }; var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        BsonDocument? selected = null;
        return await CommandAsync(actor, operation, Permissions.CatalogPublicApprove, metadata, body,
            async (snapshot, tx, token) => {
                var proposal = await Rows(S.Proposals).Find(tx, F.Eq("_id", proposalId)).SingleOrDefaultAsync(token) ?? throw CatalogPublicSampleBson.Invalid("resource_unavailable");
                CatalogPublicSampleBson.Require(proposal); selected = proposal;
                // The proposer never decides their own proposal, whatever else they hold.
                if (proposal["proposedBy"].AsInt64 == actor.UserId) throw new AuthorizationDeniedException("self_approval");
                AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.CatalogPublicApprove, clock.UtcNow));
                var context = new Context(actor, snapshot, tx, Permissions.CatalogPublicApprove, CatalogRepository.Owner(proposal["ownerScope"].AsBsonDocument));
                _ = Require(context);
                return (context, new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64));
            },
            async (context, token) => {
                var proposal = selected ?? throw CatalogPublicSampleBson.Broken();
                if (proposal["state"] != "pending" || proposal["revision"].AsInt64 != 1)
                    throw new PersistenceConflictException("catalog_public_sample_decided", "This proposal already has a decision");
                var now = Now(); if (now < CatalogRepository.Date(proposal["proposedAt"])) throw CatalogPublicSampleBson.Broken();
                var adds = proposal["adds"].AsBsonArray.Select(value => value.AsInt64).ToArray();
                var removes = proposal["removes"].AsBsonArray.Select(value => value.AsInt64).ToArray();
                BsonValue setRevision = BsonNull.Value;
                if (approve) {
                    await RequireChangeAsync(context.Tx, adds, removes, false, token);
                    // One counter orders every approval, so two checkers can never push the set past the limit together.
                    var counter = await Rows("idCounters").FindOneAndUpdateAsync(context.Tx, F.Eq("_id", S.SetCounter) & F.Lt("value", long.MaxValue - 1),
                        Builders<BsonDocument>.Update.Inc("value", 1L), new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, token);
                    setRevision = counter["value"].ToInt64();
                    if (adds.Length > 0) {
                        var families = await Rows("productFamilies").Find(context.Tx, F.In("_id", adds)).ToListAsync(token);
                        if (families.Count != adds.Length || families.Any(row => CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument) != context.Owner))
                            throw CatalogPublicSampleBson.Changed();
                        await Rows(S.Samples).InsertManyAsync(context.Tx, adds.Select(familyId => new BsonDocument { { "_id", familyId }, { "schemaVersion", 1 },
                            { "ownerScope", CatalogRepository.Owner(context.Owner) }, { "revision", setRevision }, { "addedAt", now.UtcDateTime },
                            { "approvedProposalId", proposalId }, { "migratedFrom", BsonNull.Value } }), cancellationToken: token);
                    }
                    if (removes.Length > 0 && (await Rows(S.Samples).DeleteManyAsync(context.Tx, F.In("_id", removes), cancellationToken: token)).DeletedCount != removes.Length)
                        throw CatalogPublicSampleBson.Changed();
                }
                var after = new BsonDocument(proposal.Where(item => item.Name is not ("state" or "revision" or "decision" or "snapshotHash")));
                after["state"] = approve ? "approved" : "rejected"; after["revision"] = 2L;
                after["decision"] = new BsonDocument { { "outcome", approve ? "approved" : "rejected" }, { "decidedBy", actor.UserId },
                    { "decidedAt", now.UtcDateTime }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey },
                    { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId },
                    { "setRevision", setRevision } };
                after = new BsonDocument(proposal.Names.Where(name => name != "snapshotHash").Select(name => new BsonElement(name, after[name])));
                after.Add("snapshotHash", CatalogPublicSampleBson.Hash(after));
                if ((await Rows(S.Proposals).ReplaceOneAsync(context.Tx, F.Eq("_id", proposalId) & F.Eq("revision", 1L) & F.Eq("snapshotHash", proposal["snapshotHash"]),
                    after, cancellationToken: token)).ModifiedCount != 1) throw CatalogPublicSampleBson.Changed();
                var result = new BsonDocument { { "proposalId", proposalId }, { "state", after["state"] } };
                await EffectsAsync(context.Tx, auditId, operation, actor, now, after, result, token);
                return result;
            }, (record, actorId) => record["decision"].IsBsonDocument && record["decision"]["decidedBy"].AsInt64 == actorId &&
                record["decision"]["requestKey"] == metadata.IdempotencyKey && record["decision"]["outcome"] == (approve ? "approved" : "rejected"), ct);
    }

    private async Task EffectsAsync(IClientSessionHandle tx, long auditId, string operation, ActorContext actor, DateTimeOffset at,
        BsonDocument record, BsonDocument result, CancellationToken ct)
    {
        await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", auditId }, { "schemaVersion", 1 }, { "kind", S.Prefix + "command" }, { "operation", operation },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "createdAt", at.UtcDateTime },
            { "record", record.DeepClone() }, { "result", result.DeepClone() } }, cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", S.Prefix + "command:" + auditId.ToString(CultureInfo.InvariantCulture) },
            { "kind", S.Prefix + "changed" }, { "createdAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 },
            { "dueAt", at.UtcDateTime }, { "payload", result.DeepClone() } }, cancellationToken: ct);
    }

    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, string permission, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<(Context Context, ApprovalRequest? Approval)>> target,
        Func<Context, CancellationToken, Task<BsonDocument>> write, Func<BsonDocument, long, bool> recorded, CancellationToken ct)
    {
        Context? captured = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            var result = await target(snapshot, tx, token); captured = result.Context;
            var query = Require(captured);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(captured.Resource)).Select(clause => clause.GrantId).ToImmutableArray()),
                ScopePolicy.RequiredNodes(captured.Resource, snapshot.Directory), permission, new(captured.Resource, result.Approval));
        }, (_, token) => write(captured ?? throw CatalogPublicSampleBson.Broken(), token), ct, async (_, tx, token) => {
            var context = captured ?? throw CatalogPublicSampleBson.Broken();
            // A replay returns the original result only while its exact record still stands and the caller still holds authority.
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) &
                F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(token) ?? throw CatalogPublicSampleBson.Broken();
            var record = await Rows(S.Proposals).Find(tx, F.Eq("_id", receipt["result"]["proposalId"])).SingleOrDefaultAsync(token) ?? throw CatalogPublicSampleBson.Broken();
            if (!recorded(CatalogPublicSampleBson.Require(record), actor.UserId)) throw CatalogPublicSampleBson.Broken();
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
            if (!live) throw CatalogPublicSampleBson.Invalid("stale_or_inactive_session");
        }
        _ = Require(context); ct.ThrowIfCancellationRequested();
    }
}

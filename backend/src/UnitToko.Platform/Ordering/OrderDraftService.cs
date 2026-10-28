using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderDraftBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderDraftView(LongId Id, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, OrderDraftInput Input);
public sealed record OrderDraftPage(ImmutableArray<OrderDraftView> Items, bool HasMore);

/// <summary>Private, versioned buyer drafts. Save has no order submission, stock reservation, payment or external message effect.</summary>
public sealed partial class OrderDraftService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly OrderQuoteService authority;
    private readonly CatalogDefinitionQueryService catalog;
    public OrderDraftService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock) {
        this.database = database; this.ids = ids; this.clock = clock; authorization = new(database, unit, clock);
        authority = new(database, unit, clock); catalog = new(database, authorization, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static object Body(long? draftId, long expectedRevision, OrderDraftInput input) => new { draftId, expectedRevision, input };
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
            throw new DomainValidationException("invalid_order_draft_key", "Use a bounded original request reference");
        foreach (var field in new[] { (metadata.Reason, 1000), (metadata.CorrelationId, 128) })
            if (string.IsNullOrWhiteSpace(field.Item1) || field.Item1.Trim() != field.Item1 || field.Item1.Length > field.Item2 || field.Item1.Any(char.IsControl))
                throw new DomainValidationException("invalid_order_draft_metadata", "Use bounded audit metadata");
    }
    private async Task StorageAsync(CancellationToken ct) {
        // Even compatible partial DDL must not activate a writer before its migration completes.
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw B.Invalid("order_draft_storage_unavailable");
        foreach (var name in OrderDraftSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, OrderDraftSchemas.Validator(name), OrderDraftSchemas.Key(name), ct);
        await OrderDraftDiscardSchemas.RequireStorageAsync(database, ct);
        await OrderDraftSubmissionSchemas.RequireStorageAsync(database, ct);
    }
    public async Task<BsonDocument> SaveAsync(ActorContext actor, long? draftId, long expectedRevision, OrderDraftInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Metadata(metadata); input = OrderDraftPolicy.Copy(input);
        if (draftId is null ? expectedRevision != 0 : draftId <= 0 || expectedRevision is <= 0 or long.MaxValue)
            throw new DomainValidationException("invalid_order_draft_revision", "Choose the current draft revision");
        await StorageAsync(ct);
        var selectedId = draftId ?? (await ids.NextAsync(B.Heads, ct)).Value;
        var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = Body(draftId, expectedRevision, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? previous = null; var wrote = false;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (snapshot, tx, token) => {
            wrote = false; previous = null; authority.Require(actor, snapshot, input.Context);
            var originalVersions = await Rows(B.Versions).Find(tx, F.Eq("createdBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (originalVersions.Count > 1) throw B.Invalid();
            // Missing receipt is corruption, not permission to recreate a previously saved draft under its old key.
            if (originalVersions.Count == 1) _ = await VersionAsync(tx, originalVersions[0]["_id"].AsInt64, token);
            if (draftId.HasValue) {
                previous = await HeadAsync(actor, snapshot, tx, draftId.Value, token);
                if (B.Context(previous["context"].AsBsonDocument) != input.Context) throw B.Invalid("order_draft_branch_locked");
                // A discarded draft is hidden; editing it would silently resurrect it. Restore first.
                if (await DiscardRowAsync(tx, previous, token) is not null) throw Discarded();
                // F-030: a draft that has become an order is closed; editing it would turn it back into a keranjang.
                if (await SubmittedAsync(database, tx, draftId.Value, token)) throw Submitted();
            }
            var resource = input.Context.Resource(actor.UserId);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.OrderSubmit, resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(resource, snapshot.Directory), Permissions.OrderSubmit, new(resource));
        }, async (tx, token) => {
            if (previous is not null && previous["revision"].AsInt64 != expectedRevision) throw Changed();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now == default || previous is not null && now < B.Time(previous["updatedAt"])) throw B.Invalid();
            var version = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "kind", "ordering.draft.version" },
                { "draftId", selectedId }, { "revision", expectedRevision + 1 }, { "createdBy", actor.UserId }, { "recordedAt", now.UtcDateTime },
                { "input", B.Input(input) }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            await Rows(B.Versions).InsertOneAsync(tx, version, cancellationToken: token);
            var head = new BsonDocument { { "_id", selectedId }, { "schemaVersion", 1 }, { "kind", "ordering.draft" },
                { "versionId", versionId }, { "revision", expectedRevision + 1 }, { "createdBy", actor.UserId }, { "context", B.Context(input.Context) },
                { "createdAt", previous is null ? new BsonDateTime(now.UtcDateTime) : previous["createdAt"] }, { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
            if (previous is null) await Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(B.Heads).ReplaceOneAsync(tx, previous, head, cancellationToken: token)).ModifiedCount != 1) throw Changed();
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(version), cancellationToken: token);
            // Private IDs only; no delivery address or contact details are sent to a notification processor.
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "sourceKey", "ordering.draft.saved:" + versionId }, { "kind", "ordering.draft.saved" }, { "payload", B.Result(version) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            wrote = true; return B.Result(version);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await ReceiptAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw B.Invalid();
            var version = await ReceiptVersionAsync(actor, receipt, tx, token);
            if (B.Input(version["input"].AsBsonDocument).Context != input.Context) throw B.Invalid();
            _ = await HeadAsync(actor, snapshot, tx, version["draftId"].AsInt64, token);
            if (wrote) {
                // Validate current selections after every persistence await, while the entire write is still invisible.
                // Failure aborts draft/version/audit/receipt/outbox together. Replay reads the original private draft result.
                var facts = await catalog.ReadSelectedAsync(actor, snapshot, tx, input.Context.Scope,
                    input.Selections.Select(line => line.SkuId.Value).ToImmutableArray(),
                    fenceToken => authority.FenceAsync(actor, snapshot, tx, [input.Context], fenceToken), token);
                _ = OrderQuotePolicy.Create(input.Selections, facts.Select(value => new OrderCatalogFact(value.Family, value.Sku, value.Definition, value.CurrentPrice)).ToImmutableArray());
                authority.Require(actor, snapshot, input.Context);
            } else await authority.FenceAsync(actor, snapshot, tx, [input.Context], token);
        });
    }
    public async Task<OrderDraftView> ReadAsync(ActorContext actor, long draftId, CancellationToken ct = default) {
        if (draftId <= 0) throw B.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var head = await HeadAsync(actor, snapshot, tx, draftId, token); var version = await VersionAsync(tx, head["versionId"].AsInt64, token);
            if (await DiscardRowAsync(tx, head, token) is not null) throw Discarded();
            await TouchAsync(tx, head, token); var input = B.Input(version["input"].AsBsonDocument);
            await authority.FenceAsync(actor, snapshot, tx, [input.Context], token);
            return new OrderDraftView(new(draftId), head["revision"].AsInt64, B.Time(head["createdAt"]), B.Time(head["updatedAt"]), input);
        }, ct);
    }
    public async Task<OrderDraftPage> ListAsync(ActorContext actor, OrderBuyingContext context, long? beforeId = null, int limit = 25, CancellationToken ct = default) {
        context.Validate(); if (beforeId is <= 0 || limit is < 1 or > 25) throw B.Invalid("invalid_order_draft_page"); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            authority.Require(actor, snapshot, context);
            var filter = F.Eq("createdBy", actor.UserId) & F.Eq("context.organizationId", context.OrganizationId.Value) &
                F.Eq("context.branchId", context.BranchId.Value) & F.Eq<BsonValue>("context.divisionId", context.DivisionId is { } division ? new BsonInt64(division.Value) : BsonNull.Value);
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            // Discarded drafts are hidden here and listed by ListDiscardedAsync; drafts whose packages have all become orders (F-030)
            // are never listed again, so the keranjang moves on to a fresh draft. Both are excluded before the page limit so pages stay full.
            var rows = await Rows(B.Heads).Aggregate(tx).Match(filter).Sort(new BsonDocument("_id", -1))
                .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument { { "from", OrderDraftDiscardSchemas.Collection },
                    { "localField", "_id" }, { "foreignField", "_id" }, { "as", "discard" } }))
                .Match(F.Size("discard", 0))
                .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument { { "from", OrderDraftSubmissionSchemas.Collection },
                    { "localField", "_id" }, { "foreignField", "draftId" }, { "as", "submitted" } }))
                .Match(F.Ne("submitted.closesDraft", true)).Limit(limit + 1).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<OrderDraftView>();
            foreach (var candidate in rows.Take(limit)) {
                var head = await HeadAsync(actor, snapshot, tx, candidate["_id"].AsInt64, token);
                var version = await VersionAsync(tx, head["versionId"].AsInt64, token); await TouchAsync(tx, head, token);
                result.Add(new(new(head["_id"].AsInt64), head["revision"].AsInt64, B.Time(head["createdAt"]), B.Time(head["updatedAt"]), B.Input(version["input"].AsBsonDocument)));
            }
            await authority.FenceAsync(actor, snapshot, tx, [context], token);
            return new OrderDraftPage(result.ToImmutable(), rows.Count > limit);
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Read original draft result", "order-draft-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.OrderSubmit, clock.UtcNow));
            var receipt = await ReceiptAsync(actor, key, tx, token);
            if (receipt is null) {
                // Unknown stays unknown; recovery never invents a replacement save.
                await authority.FenceAsync(actor, snapshot, tx, [], token); return null;
            }
            var version = await ReceiptVersionAsync(actor, receipt, tx, token);
            var head = await HeadAsync(actor, snapshot, tx, version["draftId"].AsInt64, token); await TouchAsync(tx, head, token);
            await authority.FenceAsync(actor, snapshot, tx, [B.Input(version["input"].AsBsonDocument).Context], token);
            return B.Result(version);
        }, ct);
    }
    private Task<BsonDocument?> ReceiptAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) =>
        Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct)!;
    private async Task<BsonDocument> ReceiptVersionAsync(ActorContext actor, BsonDocument receipt, IClientSessionHandle tx, CancellationToken ct) {
        try {
            var result = receipt["result"].AsBsonDocument;
            var version = await VersionAsync(tx, result["versionId"].AsInt64, ct);
            if (version["createdBy"].AsInt64 != actor.UserId || !CatalogSchemas.SemanticEquals(result, B.Result(version)) ||
                receipt["key"] != version["requestKey"] || receipt["bodyHash"] != version["bodyHash"]) throw B.Invalid(); return version;
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException) { throw B.Invalid(); }
    }
    private async Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var version = await Rows(B.Versions).Find(tx, F.Eq("_id", id) & OrderDraftSchemas.Validator(B.Versions)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var input = B.Input(version["input"].AsBsonDocument); var revision = version["revision"].AsInt64; var draftId = version["draftId"].AsInt64;
        var hash = AuthorizationStore.CommandHash(Body(revision == 1 ? null : draftId, revision - 1, input), version["reason"].AsString);
        if (hash != version["bodyHash"].AsString) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", version["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, B.Audit(version))) throw B.Invalid();
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", version["createdBy"]) & F.Eq("operation", B.Operation) & F.Eq("key", version["requestKey"])).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != hash || !receipt.TryGetValue("result", out var result) ||
            !CatalogSchemas.SemanticEquals(result, B.Result(version))) throw B.Invalid();
        return version;
    }
    private async Task<BsonDocument> HeadAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await Rows(B.Heads).Find(tx, F.Eq("_id", id) & F.Eq("createdBy", actor.UserId) & OrderDraftSchemas.Validator(B.Heads)).SingleOrDefaultAsync(ct)
            ?? throw B.Invalid("order_draft_unavailable");
        var context = B.Context(head["context"].AsBsonDocument); authority.Require(actor, snapshot, context);
        var version = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        var latest = await Rows(B.Versions).Find(tx, F.Eq("draftId", id)).Sort(new BsonDocument("revision", -1)).FirstOrDefaultAsync(ct);
        if (latest is null || latest["_id"] != version["_id"] || version["revision"] != head["revision"] || version["draftId"] != head["_id"] ||
            version["createdBy"] != head["createdBy"] || version["recordedAt"] != head["updatedAt"] || B.Input(version["input"].AsBsonDocument).Context != context) throw B.Invalid();
        return head;
    }
    private async Task TouchAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        if (head["fence"].AsInt64 >= long.MaxValue - 1) throw B.Invalid();
        if ((await Rows(B.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    private static PersistenceConflictException Changed() => new("order_draft_changed", "Draft changed; reload before saving");
}

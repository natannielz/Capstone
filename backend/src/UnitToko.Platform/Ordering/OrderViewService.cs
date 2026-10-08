using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderViewBson;

namespace UnitToko.Platform.Ordering;

public sealed record SavedOrderView(LongId Id, long Revision, string Name, OrderHistoryFilter Filter, DateTimeOffset UpdatedAt);
public sealed record SavedOrderViewPage(ImmutableArray<SavedOrderView> Items, long? NextId);

/// <summary>Account-owned query preferences. Every read/write uses current order-read authority;
/// a saved context grants no access, and a removed head preserves original operation recovery.</summary>
public sealed class OrderViewService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly OrderQuoteService authority = new(database, unit, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Changed() => new("order_view_changed", "Reload the current saved view");
    private static void Metadata(AuthorizationCommandMetadata value) {
        AuthorizationStore.ValidateMetadata(value);
        if (value.IdempotencyKey.Length > 128 || value.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            value.Reason.Trim() != value.Reason || value.Reason.Length > 1000 || value.Reason.Any(char.IsControl) ||
            value.CorrelationId.Trim() != value.CorrelationId || value.CorrelationId.Length > 128 || value.CorrelationId.Any(char.IsControl)) throw OrderHistoryFilter.Invalid();
    }
    private async Task StorageAsync(CancellationToken ct) {
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw B.Invalid();
        foreach (var name in OrderViewSchemas.Collections) {
            BsonDocument[] keys = name == B.Versions ? [OrderViewSchemas.Key(name), OrderViewSchemas.RequestKey()] : [OrderViewSchemas.Key(name)];
            await InventoryRepository.RequireStorageAsync(database, name, OrderViewSchemas.Validator(name), keys, ct);
        }
    }
    private AuthorizationQuery Require(ActorContext actor, AuthorizationSnapshot snapshot, OrderHistoryFilter? filter = null) {
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderRead, clock.UtcNow); AuthorizationStore.Require(query);
        if (filter?.Context is { } context) authority.Require(actor, snapshot, context, Permissions.OrderRead);
        return query;
    }
    private Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, OrderHistoryFilter? filter, CancellationToken ct) =>
        authority.FenceAsync(actor, snapshot, tx, filter?.Context is { } context ? [context] : [], ct, Permissions.OrderRead);

    public async Task<BsonDocument> ChangeAsync(ActorContext actor, string action, long? viewId, long revision, string? name,
        OrderHistoryFilter? filter, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata);
        if (action is not ("create" or "rename" or "remove") || (action == "create" ? viewId is not null || revision != 0 || filter is null : viewId is null or <= 0 || revision is <= 0 or long.MaxValue || filter is not null) ||
            (action == "remove" ? name is not null : name is null)) throw OrderHistoryFilter.Invalid();
        if (name is not null) name = OrderHistoryFilter.Name(name); filter?.Validate(); await StorageAsync(ct);
        var selected = viewId ?? (await ids.NextAsync(B.Heads, ct)).Value;
        var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.Body(action, viewId, revision, name, filter); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? previous = null; OrderHistoryFilter? actualFilter = filter;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (snapshot, tx, token) => {
            previous = null; actualFilter = filter;
            Require(actor, snapshot);
            // Original owners without their receipt are corruption, not an uncommitted retry.
            var original = await Rows(B.Versions).Find(tx, F.Eq("createdBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (original.Count > 1) throw B.Invalid();
            if (original.Count == 1) await VersionAsync(tx, original[0]["_id"].AsInt64, token);
            else if (await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("requestKey", metadata.IdempotencyKey)).AnyAsync(token)) throw B.Invalid();
            if (viewId is { } id) { previous = await HeadAsync(actor, snapshot, tx, id, token); actualFilter = B.Filter(previous["filter"]); }
            var query = Require(actor, snapshot, actualFilter);
            var clauses = query.Clauses.Where(clause => actualFilter?.Context is not { } context || clause.Matches(context.Resource(actor.UserId))).ToArray();
            return new(new AuthorizationDecision(true, "allowed", [.. clauses.Select(clause => clause.GrantId).Distinct()]),
                [.. clauses.SelectMany(clause => clause.RequiredNodes).Distinct()], Permissions.OrderRead);
        }, async (tx, token) => {
            if (previous is not null && (previous["removed"].AsBoolean || previous["revision"].AsInt64 != revision)) throw Changed();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now == default || now.Offset != TimeSpan.Zero || previous is not null && now < OrderDraftBson.Time(previous["updatedAt"])) throw B.Invalid();
            var version = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "viewId", selected }, { "revision", revision + 1 },
                { "createdBy", actor.UserId }, { "name", name ?? previous!["name"].AsString }, { "filter", B.Filter(actualFilter!) }, { "action", action },
                { "recordedAt", now.UtcDateTime }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash },
                { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            await Rows(B.Versions).InsertOneAsync(tx, version, cancellationToken: token);
            var head = new BsonDocument { { "_id", selected }, { "schemaVersion", 1 }, { "versionId", versionId }, { "revision", revision + 1 },
                { "createdBy", actor.UserId }, { "name", version["name"] }, { "filter", version["filter"].DeepClone() }, { "removed", action == "remove" },
                { "createdAt", previous is null ? new BsonDateTime(now.UtcDateTime) : previous["createdAt"] }, { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
            if (previous is null) await Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(B.Heads).ReplaceOneAsync(tx, previous, head, cancellationToken: token)).ModifiedCount != 1) throw Changed();
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(version), cancellationToken: token); return B.Result(version);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await ReceiptAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw B.Invalid();
            var version = await ReceiptVersionAsync(actor, receipt, tx, token);
            var head = await HeadAsync(actor, snapshot, tx, version["viewId"].AsInt64, token);
            await TouchAsync(tx, head, token); await FenceAsync(actor, snapshot, tx, B.Filter(version["filter"]), token);
        });
    }
    public async Task<SavedOrderViewPage> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw OrderHistoryFilter.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Require(actor, snapshot); var allowed = query.Clauses;
            // Query preferences are private to the account. Revoked contexts are omitted, never widened to all contexts.
            var filter = F.Eq("createdBy", actor.UserId) & F.Eq("removed", false);
            if (beforeId is not null) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows(B.Heads).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var result = ImmutableArray.CreateBuilder<SavedOrderView>();
            foreach (var raw in rows.Take(25)) {
                var currentFilter = B.Filter(raw["filter"]);
                if (currentFilter.Context is { } context && !allowed.Any(clause => clause.Matches(context.Resource(actor.UserId)))) continue;
                var head = await HeadAsync(actor, snapshot, tx, raw["_id"].AsInt64, token); await TouchAsync(tx, head, token); result.Add(View(head));
            }
            await FenceAsync(actor, snapshot, tx, null, token);
            return new SavedOrderViewPage(result.ToImmutable(), rows.Count > 25 ? rows[24]["_id"].AsInt64 : null);
        }, ct);
    }
    public async Task<SavedOrderView> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw OrderHistoryFilter.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var head = await HeadAsync(actor, snapshot, tx, id, token); if (head["removed"].AsBoolean) throw B.Invalid();
            await TouchAsync(tx, head, token); await FenceAsync(actor, snapshot, tx, B.Filter(head["filter"]), token); return View(head);
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original saved order view", "order-view-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            Require(actor, snapshot); var receipt = await ReceiptAsync(actor, key, tx, token);
            if (receipt is null) {
                if (await Rows(B.Versions).Find(tx, F.Eq("createdBy", actor.UserId) & F.Eq("requestKey", key)).AnyAsync(token) ||
                    await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("requestKey", key)).AnyAsync(token)) throw B.Invalid();
                await FenceAsync(actor, snapshot, tx, null, token); return null;
            }
            var version = await ReceiptVersionAsync(actor, receipt, tx, token);
            var head = await HeadAsync(actor, snapshot, tx, version["viewId"].AsInt64, token); await TouchAsync(tx, head, token);
            await FenceAsync(actor, snapshot, tx, B.Filter(version["filter"]), token); return B.Result(version);
        }, ct);
    }
    private Task<BsonDocument?> ReceiptAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) =>
        Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct)!;
    private async Task<BsonDocument> ReceiptVersionAsync(ActorContext actor, BsonDocument receipt, IClientSessionHandle tx, CancellationToken ct) {
        try {
            var version = await VersionAsync(tx, receipt["result"]["versionId"].AsInt64, ct);
            if (version["createdBy"] != actor.UserId || receipt["key"] != version["requestKey"] || receipt["bodyHash"] != version["bodyHash"] || receipt["result"] != B.Result(version)) throw B.Invalid();
            return version;
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException) { throw B.Invalid(); }
    }
    private async Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var version = await Rows(B.Versions).Find(tx, F.Eq("_id", id) & OrderViewSchemas.Validator(B.Versions)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        _ = OrderHistoryFilter.Name(version["name"].AsString); _ = B.Filter(version["filter"]);
        if (AuthorizationStore.CommandHash(B.Body(version), version["reason"].AsString) != version["bodyHash"].AsString) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", version["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", version["createdBy"]) & F.Eq("operation", B.Operation) & F.Eq("key", version["requestKey"])).SingleOrDefaultAsync(ct);
        if (audit != B.Audit(version) || receipt is null || receipt.ElementCount != 8 || receipt["schemaVersion"] != 1 || !receipt["_id"].IsObjectId ||
            receipt["bodyHash"] != version["bodyHash"] || receipt["result"] != B.Result(version) || !receipt["createdAt"].IsBsonDateTime ||
            OrderDraftBson.Time(receipt["createdAt"]) > OrderDraftBson.Time(version["recordedAt"])) throw B.Invalid();
        await ExactAsync(tx, B.Versions, version, ct); await ExactAsync(tx, "auditEvents", audit!, ct); await ExactAsync(tx, "idempotencyRecords", receipt, ct); return version;
    }
    private async Task<BsonDocument> HeadAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await Rows(B.Heads).Find(tx, F.Eq("_id", id) & F.Eq("createdBy", actor.UserId) & OrderViewSchemas.Validator(B.Heads)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        Require(actor, snapshot, B.Filter(head["filter"]));
        var chain = await Rows(B.Versions).Find(tx, F.Eq("viewId", id)).Sort(new BsonDocument("revision", 1)).ToListAsync(ct);
        if (chain.Count != head["revision"].AsInt64 || chain.Count == 0) throw B.Invalid();
        BsonDocument? previous = null;
        for (var index = 0; index < chain.Count; index++) {
            var version = await VersionAsync(tx, chain[index]["_id"].AsInt64, ct);
            if (version != chain[index] || version["revision"] != (long)index + 1 || version["createdBy"] != actor.UserId || version["viewId"] != id ||
                version["filter"] != head["filter"] || (index == 0 ? version["action"] != "create" || version["recordedAt"] != head["createdAt"] :
                version["action"] == "create" || previous!["action"] == "remove" || version["recordedAt"].CompareTo(previous["recordedAt"]) < 0 ||
                version["action"] == "remove" && version["name"] != previous["name"])) throw B.Invalid();
            previous = version;
        }
        if (previous!["_id"] != head["versionId"] || previous["name"] != head["name"] || previous["recordedAt"] != head["updatedAt"] ||
            head["removed"].AsBoolean != (previous["action"] == "remove")) throw B.Invalid();
        return head;
    }
    private async Task ExactAsync(IClientSessionHandle tx, string table, BsonDocument row, CancellationToken ct) {
        if ((await Rows(table).DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw Changed();
        await Rows(table).InsertOneAsync(tx, row.DeepClone().AsBsonDocument, cancellationToken: ct);
    }
    private async Task TouchAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    private static SavedOrderView View(BsonDocument head) => new(new(head["_id"].AsInt64), head["revision"].AsInt64, head["name"].AsString,
        B.Filter(head["filter"]), OrderDraftBson.Time(head["updatedAt"]));
}

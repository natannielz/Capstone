using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentPreparationBson;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentPreparationView(LongId Id, long Revision, DateTimeOffset RecordedAt,
    FulfillmentOrderView AcceptedSource, ImmutableArray<ShipmentPackageSelection> Lines, bool CurrentSourceMatches);
internal sealed record PreparedShipment(BsonDocument Head, BsonDocument Version, OrderDispatchSource Source);

/// <summary>One shared, versioned preparation per accepted store suborder. Saving a partial pick plan
/// changes neither reservation nor physical stock. Dispatch must separately revalidate and commit it.</summary>
public sealed partial class ShipmentPreparationService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly OrderSubmissionService orders;
    private readonly AuthorizationStore authorization;
    internal IMongoDatabase SourceDatabase => database;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public ShipmentPreparationService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, OrderSubmissionService orders) {
        if (!ReferenceEquals(database.Client, orders.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(orders.SourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Order and fulfillment owners must join the same database transaction", nameof(orders));
        this.database = database; this.clock = clock; this.ids = ids; this.orders = orders; authorization = new(database, unit, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static object Body(long suborderId, long expectedRevision, string review, ImmutableArray<ShipmentPackageSelection> lines) => new { suborderId, expectedRevision, review, lines };
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); InventoryLedger.RequireText(metadata.Reason, 1000); InventoryLedger.RequireText(metadata.CorrelationId, 128);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw new DomainValidationException("invalid_shipment_preparation_key", "Use a bounded original request reference");
    }
    internal async Task StorageAsync(CancellationToken ct) {
        await orders.RequireFulfillmentSourceStorageAsync(ct);
        foreach (var name in ShipmentPreparationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ShipmentPreparationSchemas.Validator(name), ShipmentPreparationSchemas.Key(name), ct);
    }
    internal async Task<PreparedShipment> ReadForDispatchAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long suborderId, CancellationToken ct) {
        var source = await orders.ReadDispatchReservationsAsync(actor, snapshot, tx, suborderId, ct);
        var head = await HeadAsync(tx, suborderId, ct) ?? throw B.Invalid();
        var version = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        if (!source.Order.View.CanPrepare || source.Order.View.Review != version["source"]["review"].AsString ||
            !CatalogEqual(head["scope"], B.Scope(source.Order.Resource))) throw Changed();
        await TouchAsync(tx, head, ct);
        return new(head, version, source);
    }
    internal async Task FenceDispatchedPreparationAsync(PreparedShipment prepared, IClientSessionHandle tx, CancellationToken ct) {
        var head = await HeadAsync(tx, prepared.Head["suborderId"].AsInt64, ct) ?? throw B.Invalid();
        if (head["_id"] != prepared.Head["_id"] || head["versionId"] != prepared.Version["_id"]) throw Changed();
        await TouchAsync(tx, head, ct);
    }
    public async Task<BsonDocument> SaveAsync(ActorContext actor, long suborderId, long expectedRevision, string review,
        ImmutableArray<ShipmentPackageSelection> lines, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata); lines = ShipmentPreparationPolicy.Copy(lines);
        if (suborderId <= 0 || expectedRevision is < 0 or long.MaxValue || review is null || review.Length != 64 || review.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainValidationException("invalid_shipment_preparation", "Use the current order and preparation revision");
        await StorageAsync(ct);
        var proposedId = (await ids.NextAsync(B.Heads, ct)).Value; var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = Body(suborderId, expectedRevision, review, lines);
        BsonDocument? previous = null; FulfillmentOrderSource source = null!; var wrote = false;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (snapshot, tx, token) => {
            wrote = false;
            source = await orders.ReadForShipmentPreparationAsync(actor, snapshot, tx, suborderId, token);
            previous = await HeadAsync(tx, suborderId, token);
            var originals = await Rows(B.Versions).Find(tx, F.Eq("preparedBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (originals.Count > 1) throw B.Invalid();
            if (originals.Count == 1) _ = await VersionAsync(tx, originals[0]["_id"].AsInt64, token);
            return new(ScopePolicy.Authorize(actor, snapshot, Permissions.ShipmentPrepare, source.Resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(source.Resource, snapshot.Directory), Permissions.ShipmentPrepare, new(source.Resource));
        }, async (tx, token) => {
            if ((previous?["revision"].AsInt64 ?? 0) != expectedRevision) throw Changed();
            _ = ShipmentPreparationPolicy.Require(source.View, review, lines);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            if (now < source.View.OrderedAt || previous is not null && now < OrderDraftBson.Time(previous["updatedAt"])) throw B.Invalid();
            var id = previous?["_id"].AsInt64 ?? proposedId;
            var version = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "kind", "fulfillment.preparation.version" },
                { "preparationId", id }, { "revision", expectedRevision + 1 }, { "preparedBy", actor.UserId }, { "recordedAt", now.UtcDateTime },
                { "source", B.Source(source.View) }, { "scope", B.Scope(source.Resource) }, { "lines", B.Lines(lines) }, { "actorGrantVersion", actor.GrantVersion },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) },
                { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            await Rows(B.Versions).InsertOneAsync(tx, version, cancellationToken: token);
            var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "fulfillment.preparation" }, { "suborderId", suborderId },
                { "orderId", source.View.OrderId.Value }, { "versionId", versionId }, { "revision", expectedRevision + 1 }, { "scope", B.Scope(source.Resource) },
                { "createdAt", previous is null ? new BsonDateTime(now.UtcDateTime) : previous["createdAt"] }, { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
            if (previous is null) await Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(B.Heads).ReplaceOneAsync(tx, previous, head, cancellationToken: token)).ModifiedCount != 1) throw Changed();
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(version), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "kind", "fulfillment.preparation.saved" }, { "sourceKey", "fulfillment.preparation.saved:" + versionId }, { "payload", B.Result(version) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            wrote = true; return B.Result(version);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await ReceiptAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw B.Invalid();
            var recorded = await ReceiptVersionAsync(actor, receipt, tx, token);
            if (recorded["source"]["suborderId"] != suborderId) throw B.Invalid();
            var current = await orders.ReadForShipmentPreparationAsync(actor, snapshot, tx, suborderId, token);
            if (wrote) _ = ShipmentPreparationPolicy.Require(current.View, review, lines);
            var head = await HeadAsync(tx, suborderId, token) ?? throw B.Invalid();
            if (!CatalogEqual(head["scope"], B.Scope(current.Resource))) throw B.Invalid();
            await TouchAsync(tx, head, token); await orders.FenceShipmentPreparationAsync(actor, snapshot, tx, [current], token);
        });
    }
    public async Task<ShipmentPreparationView?> ReadAsync(ActorContext actor, long suborderId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync<ShipmentPreparationView?>(actor, async (snapshot, tx, token) => {
            var source = await orders.ReadForShipmentPreparationAsync(actor, snapshot, tx, suborderId, token);
            var head = await HeadAsync(tx, suborderId, token); ShipmentPreparationView? result = null;
            if (head is not null) {
                if (!CatalogEqual(head["scope"], B.Scope(source.Resource))) throw B.Invalid();
                var version = await VersionAsync(tx, head["versionId"].AsInt64, token); await TouchAsync(tx, head, token);
                result = new(new(head["_id"].AsInt64), head["revision"].AsInt64, OrderDraftBson.Time(version["recordedAt"]), B.Source(version["source"].AsBsonDocument),
                    B.Lines(version["lines"].AsBsonArray), source.View.CanPrepare && source.View.Review == version["source"]["review"].AsString);
            }
            await orders.FenceShipmentPreparationAsync(actor, snapshot, tx, [source], token); return result;
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original preparation", "shipment-preparation-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.ShipmentPrepare, clock.UtcNow));
            var receipt = await ReceiptAsync(actor, key, tx, token);
            if (receipt is null) { await orders.FenceShipmentPreparationAsync(actor, snapshot, tx, [], token); return null; }
            var version = await ReceiptVersionAsync(actor, receipt, tx, token);
            var source = await orders.ReadForShipmentPreparationAsync(actor, snapshot, tx, version["source"]["suborderId"].AsInt64, token);
            var head = await HeadAsync(tx, source.View.SuborderId.Value, token) ?? throw B.Invalid();
            if (!CatalogEqual(head["scope"], B.Scope(source.Resource))) throw B.Invalid();
            await TouchAsync(tx, head, token); await orders.FenceShipmentPreparationAsync(actor, snapshot, tx, [source], token);
            return B.Result(version);
        }, ct);
    }
    private Task<BsonDocument?> ReceiptAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) =>
        Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct)!;
    private async Task<BsonDocument> ReceiptVersionAsync(ActorContext actor, BsonDocument receipt, IClientSessionHandle tx, CancellationToken ct) {
        var version = await VersionAsync(tx, receipt["result"]["versionId"].AsInt64, ct);
        if (version["preparedBy"] != actor.UserId || version["requestKey"] != receipt["key"] || version["bodyHash"] != receipt["bodyHash"] || !CatalogEqual(B.Result(version), receipt["result"])) throw B.Invalid();
        return version;
    }
    private Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) => ReadVersionAsync(database, tx, id, ct);
    internal static async Task<BsonDocument> ReadVersionAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct) {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var row = await Rows(B.Versions).Find(tx, F.Eq("_id", id) & ShipmentPreparationSchemas.Validator(B.Versions)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var source = B.Source(row["source"].AsBsonDocument); var lines = B.Lines(row["lines"].AsBsonArray);
        _ = ShipmentPreparationPolicy.Require(source, source.Review, lines);
        if (row["bodyHash"] != AuthorizationStore.CommandHash(Body(source.SuborderId.Value, row["revision"].AsInt64 - 1, source.Review, lines), row["reason"].AsString)) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["preparedBy"]) & F.Eq("operation", B.Operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogEqual(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] || !CatalogEqual(receipt.GetValue("result", BsonNull.Value), B.Result(row))) throw B.Invalid();
        return row;
    }
    private async Task<BsonDocument?> HeadAsync(IClientSessionHandle tx, long suborderId, CancellationToken ct) {
        var rows = await Rows(B.Heads).Find(tx, F.Eq("suborderId", suborderId)).Limit(2).ToListAsync(ct);
        if (rows.Count == 0) {
            if (await Rows(B.Versions).Find(tx, F.Eq("source.suborderId", suborderId)).AnyAsync(ct)) throw B.Invalid();
            return null;
        }
        if (rows.Count != 1) throw B.Invalid(); var head = rows[0];
        if (!await Rows(B.Heads).Find(tx, F.Eq("_id", head["_id"]) & ShipmentPreparationSchemas.Validator(B.Heads)).AnyAsync(ct)) throw B.Invalid();
        var version = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        var latest = await Rows(B.Versions).Find(tx, F.Eq("preparationId", head["_id"])).Sort(new BsonDocument("revision", -1)).FirstOrDefaultAsync(ct);
        if (latest is null || latest["_id"] != version["_id"] || version["preparationId"] != head["_id"] || version["revision"] != head["revision"] ||
            version["source"]["suborderId"] != suborderId || version["source"]["orderId"] != head["orderId"] || version["recordedAt"] != head["updatedAt"] || !CatalogEqual(head["scope"], version["scope"])) throw B.Invalid();
        return head;
    }
    private async Task TouchAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    private static bool CatalogEqual(BsonValue left, BsonValue right) => CatalogSchemas.SemanticEquals(left, right);
    private static PersistenceConflictException Changed() => new("shipment_preparation_changed", "Reload the saved preparation before changing its quantities");
}

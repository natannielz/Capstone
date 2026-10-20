using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Fulfillment.ShipmentCourierRecords;

namespace UnitToko.Platform.Fulfillment;

public sealed class ShipmentCourierService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly ShipmentQueryService shipments;
    private readonly ShipmentReceiptService receipts;
    private readonly AuthorizationStore authorization;
    private readonly ShipmentCourierEligibility couriers;
    private readonly R records;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    public ShipmentCourierService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        ShipmentQueryService shipments, ShipmentReceiptService receipts) {
        if (new[] { shipments.SourceDatabase, receipts.SourceDatabase }.Any(source => !ReferenceEquals(source.Client, database.Client) ||
            !source.DatabaseNamespace.Equals(database.DatabaseNamespace))) throw new ArgumentException("Courier assignment owners must share one transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.shipments = shipments; this.receipts = receipts;
        authorization = new(database, unit, clock); couriers = new(database, authorization, clock); records = new(database, clock);
    }
    private async Task<ShipmentCourierTask> ViewAsync(IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) {
        var assignment = source.Assignment ?? throw R.Invalid(); assignment.Require(tx);
        var row = assignment.Shipment; var received = await receipts.HasRecordedReceiptAsync(tx, source, ct);
        return new(new(row["_id"].AsInt64), new(row["orderId"].AsInt64), new(row["suborderId"].AsInt64),
            new(row["numberEntity"].AsInt64), new(row["numberChannel"].AsInt64), checked((int)row["numberYear"].AsInt64), row["number"].AsInt64,
            new(row["courierId"].AsInt64), row["courierName"].AsString, assignment.View, received,
            !received && assignment.Revision <= R.MaximumChanges, [.. assignment.Events.Select(R.View)]);
    }
    private async Task CompleteAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) {
        await shipments.CompleteAssignmentAsync(tx, source, ct);
        await shipments.FenceAsync(actor, authority, tx, [source.Resource], ct, Permissions.ShipmentAssign);
        (source.Assignment ?? throw R.Invalid()).Require(tx);
    }
    public async Task<ShipmentCourierTask> ReadAsync(ActorContext actor, long shipmentId, CancellationToken ct = default) {
        await shipments.StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await shipments.AssignmentSourceAsync(actor, authority, tx, shipmentId, token);
            var view = await ViewAsync(tx, source, token); await CompleteAsync(actor, authority, tx, source, token); return view;
        }, ct);
    }
    public async Task<(ImmutableArray<ShipmentCourierTask> Items, bool HasMore)> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw R.Invalid(); await shipments.StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var rows = await shipments.CurrentRowsAsync(actor, authority, tx, Permissions.ShipmentAssign, beforeId, null, null, token);
            var items = ImmutableArray.CreateBuilder<ShipmentCourierTask>(); var sources = new List<ShipmentDocumentSource>();
            foreach (var row in rows) {
                var source = await shipments.AssignmentSourceAsync(actor, authority, tx, row["_id"].AsInt64, token);
                items.Add(await ViewAsync(tx, source, token)); sources.Add(source);
            }
            foreach (var source in sources) await CompleteAsync(actor, authority, tx, source, token);
            if (sources.Count == 0) await shipments.FenceAsync(actor, authority, tx, [], token, Permissions.ShipmentAssign);
            return (items.Take(25).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
    public async Task<(ImmutableArray<ShipmentCourierChoice> Items, bool HasMore)> CouriersAsync(ActorContext actor, long shipmentId, long? beforeId = null, CancellationToken ct = default) {
        await shipments.StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await shipments.AssignmentSourceAsync(actor, authority, tx, shipmentId, token);
            var targets = await couriers.ListAsync(actor, authority, tx, source.Resource, beforeId, token);
            await CompleteAsync(actor, authority, tx, source, token);
            foreach (var target in targets) couriers.RequireCurrent(actor, authority, source.Resource, target);
            return (targets.Take(25).Select(target => new ShipmentCourierChoice(new(target.UserId), target.Name)).ToImmutableArray(), targets.Length > 25);
        }, ct);
    }
    private static string Review(ShipmentDocumentSource source, ShipmentCourierTarget target) => R.Review(
        (source.Assignment ?? throw R.Invalid()).Head, source.Snapshot["dispatchHash"], target.UserId, target.Name, target.GrantVersion, target.ProfileVersion);
    private static void Expected(ShipmentDocumentSource source, long revision, long courierId) {
        var assignment = source.Assignment ?? throw R.Invalid();
        if (revision != assignment.Revision || revision > R.MaximumChanges || courierId == assignment.CourierId) throw R.Changed();
    }
    public async Task<ShipmentCourierPreview> PreviewAsync(ActorContext actor, long shipmentId, long expectedRevision, long courierId, CancellationToken ct = default) {
        await shipments.StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await shipments.AssignmentSourceAsync(actor, authority, tx, shipmentId, token); Expected(source, expectedRevision, courierId);
            var task = await ViewAsync(tx, source, token); if (task.Received) throw new PersistenceConflictException("shipment_already_received", "A verified receipt closes courier reassignment");
            var target = await couriers.ReadAsync(actor, authority, tx, source.Resource, courierId, token);
            var review = Review(source, target); await couriers.FenceAsync(actor, authority, tx, source.Resource, target, token);
            await CompleteAsync(actor, authority, tx, source, token); couriers.RequireCurrent(actor, authority, source.Resource, target);
            return new ShipmentCourierPreview(task, new(courierId), target.Name, review);
        }, ct);
    }
    public async Task<ShipmentCourierChange> HandoverAsync(ActorContext actor, long shipmentId, long expectedRevision, long courierId, string review,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (shipmentId <= 0 || expectedRevision <= 0 || courierId <= 0 || review is null || review.Length != 64 || review.Any(c => !char.IsAsciiHexDigitLower(c))) throw R.Invalid();
        await shipments.StorageAsync(ct); var body = R.Body(shipmentId, expectedRevision, courierId, review);
        ShipmentDocumentSource source = null!; ShipmentCourierTarget? target = null; BsonDocument? final = null;
        await authorization.ExecuteCommandAsync(actor, R.Operation, metadata, body, async (authority, tx, token) => {
            target = null; final = null;
            source = await shipments.AssignmentSourceAsync(actor, authority, tx, shipmentId, token);
            var old = await Rows(R.Events).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
            if (old is not null) {
                if (old["shipmentId"] != shipmentId || old["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw R.Invalid();
                final = (source.Assignment ?? throw R.Invalid()).Events.SingleOrDefault(row => row["_id"] == old["_id"]) ?? throw R.Invalid();
            } else {
                Expected(source, expectedRevision, courierId);
                if (await receipts.HasRecordedReceiptAsync(tx, source, token)) throw new PersistenceConflictException("shipment_already_received", "A verified receipt closes courier reassignment");
                target = await couriers.ReadAsync(actor, authority, tx, source.Resource, courierId, token);
                if (review != Review(source, target)) throw R.Changed();
            }
            return new(ScopePolicy.Authorize(actor, authority, Permissions.ShipmentAssign, source.Resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(source.Resource, authority.Directory), Permissions.ShipmentAssign, new(source.Resource));
        }, async (tx, token) => {
            if (final is not null || target is null) throw R.Invalid();
            var captured = source.Assignment ?? throw R.Invalid(); captured.Require(tx);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds()); if (at < captured.View.EffectiveAt) throw R.Invalid();
            var row = new BsonDocument { { "_id", (await ids.NextAsync(R.Events, token)).Value }, { "schemaVersion", 1 }, { "shipmentId", shipmentId },
                { "revision", expectedRevision + 1 }, { "previousHash", captured.Head["eventHash"] }, { "previousCourierId", captured.CourierId },
                { "courierId", target.UserId }, { "courierName", target.Name }, { "courierGrantVersion", target.GrantVersion }, { "courierProfileVersion", target.ProfileVersion },
                { "anchorHash", R.Hash(captured.Anchor) }, { "dispatchHash", captured.Anchor["dispatchHash"] }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "recordedAt", at.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) },
                { "review", review }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", (await ids.NextAsync("auditEvents", token)).Value } };
            await Rows(R.Events).InsertOneAsync(tx, row, cancellationToken: token);
            if ((await Rows(R.Heads).ReplaceOneAsync(tx, captured.Head, R.Advance(captured.Head, row), cancellationToken: token)).ModifiedCount != 1) throw R.Changed();
            await Rows("auditEvents").InsertOneAsync(tx, R.Audit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", R.Operation },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey },
                { "sourceKey", R.Operation + ":" + row["_id"].AsInt64 }, { "payload", R.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            final = row; return R.Result(row);
        }, ct, async (authority, tx, token) => {
            var capture = await records.ReadAsync(tx, (source.Assignment ?? throw R.Invalid()).Shipment, token);
            var original = capture.Events.SingleOrDefault(row => row["actorId"] == actor.UserId && row["requestKey"] == metadata.IdempotencyKey) ?? throw R.Invalid();
            if (original["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw R.Invalid(); final = original;
            source = source with { Resource = ShipmentQueryService.CurrentResource(capture), Assignment = capture };
            if (target is not null) await couriers.FenceAsync(actor, authority, tx, source.Resource, target, token);
            await CompleteAsync(actor, authority, tx, source, token);
            if (target is not null) couriers.RequireCurrent(actor, authority, source.Resource, target);
        });
        return R.View(final ?? throw R.Invalid());
    }
    public async Task<ShipmentCourierChange?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original courier handover", "courier-handover-recovery")); await shipments.StorageAsync(ct);
        return await authorization.ReadAsync<ShipmentCourierChange?>(actor, async (authority, tx, token) => {
            var row = await Rows(R.Events).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.Operation) & F.Eq("key", key)).AnyAsync(token) ||
                    await Rows("auditEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", R.Operation) & F.Eq("requestKey", key)).AnyAsync(token) ||
                    await Rows("outboxEvents").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("kind", R.Operation) & F.Eq("requestKey", key)).AnyAsync(token)) throw R.Invalid();
                await shipments.FenceAsync(actor, authority, tx, [], token, Permissions.ShipmentAssign); return null;
            }
            var source = await shipments.AssignmentSourceAsync(actor, authority, tx, row["shipmentId"].AsInt64, token);
            var original = (source.Assignment ?? throw R.Invalid()).Events.SingleOrDefault(value => value == row) ?? throw R.Invalid();
            await CompleteAsync(actor, authority, tx, source, token); return R.View(original);
        }, ct);
    }
}

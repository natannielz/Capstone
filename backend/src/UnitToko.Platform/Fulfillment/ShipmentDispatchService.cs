using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentDispatchLot(ShipmentReservationFact Reservation, DateOnly? ExpiresOn);
public sealed record ShipmentDispatchPreparation(LongId PreparationId, long PreparationRevision, FulfillmentOrderView Source,
    ImmutableArray<ShipmentPackageSelection> Lines, ImmutableArray<ShipmentDispatchLot> Lots);
public sealed record ShipmentDispatchReview(ShipmentDispatchPreparation Preparation, LongId CourierId, string CourierName,
    ImmutableArray<ShipmentLotSelection> Selected, string Review);
public sealed record ShipmentCourierChoice(LongId UserId, string DisplayName);

/// <summary>Dispatch is one owner transaction: immutable shipment, exact reserved lots, physical
/// movement, audit, original receipt and outbox. No production admission is inferred or installed.</summary>
public sealed class ShipmentDispatchService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly OrderSubmissionService orders;
    private readonly ShipmentPreparationService preparations;
    private readonly AuthorizationStore authorization;
    private readonly ShipmentCourierEligibility couriers;
    private readonly IShipmentDispatchAdmissionReader? admissionReader;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public ShipmentDispatchService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        OrderSubmissionService orders, ShipmentPreparationService preparations) : this(database, unit, ids, clock, orders, preparations, null) { }
    internal ShipmentDispatchService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        OrderSubmissionService orders, ShipmentPreparationService preparations, IShipmentDispatchAdmissionReader? reader) {
        if (!ReferenceEquals(database.Client, orders.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(orders.SourceDatabase.DatabaseNamespace) ||
            !ReferenceEquals(database.Client, preparations.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(preparations.SourceDatabase.DatabaseNamespace) ||
            reader is not null && (!ReferenceEquals(database.Client, reader.Database.Client) || !database.DatabaseNamespace.Equals(reader.Database.DatabaseNamespace)))
            throw new ArgumentException("Dispatch owners and source admission must join the same database transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.orders = orders; this.preparations = preparations; admissionReader = reader;
        authorization = new(database, unit, clock); couriers = new(database, authorization, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    public Task<(ImmutableArray<ShipmentDispatchQueueItem> Items, bool HasMore)> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) =>
        preparations.ListForDispatchAsync(actor, beforeId, ct);
    public async Task<(ImmutableArray<ShipmentCourierChoice> Items, bool HasMore)> ListCouriersAsync(ActorContext actor, long suborderId, long? beforeId = null, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var source = await orders.ReadForFulfillmentAsync(actor, snapshot, tx, suborderId, Permissions.ShipmentDispatch, token);
            var targets = await couriers.ListAsync(actor, snapshot, tx, source.Resource, beforeId, token);
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [source], Permissions.ShipmentDispatch, token);
            foreach (var target in targets) couriers.RequireCurrent(actor, snapshot, source.Resource, target);
            return (targets.Take(25).Select(target => new ShipmentCourierChoice(new(target.UserId), target.Name)).ToImmutableArray(), targets.Length > 25);
        }, ct);
    }
    private async Task StorageAsync(CancellationToken ct) {
        await preparations.StorageAsync(ct);
        await ShipmentDispatchStorage.RequireAsync(database, ct);
    }
    private static ShipmentDispatchPreparation View(PreparedShipment prepared) => new(new(prepared.Head["_id"].AsInt64), prepared.Head["revision"].AsInt64,
        prepared.Source.Order.View, ShipmentPreparationBson.Lines(prepared.Version["lines"].AsBsonArray),
        [.. prepared.Source.Reservations.Select(row => new ShipmentDispatchLot(row.Fact, row.ExpiresOn))]);
    private static void Expected(PreparedShipment prepared, long id, long revision) {
        if (prepared.Head["_id"] != id || prepared.Head["revision"] != revision) throw new PersistenceConflictException("shipment_preparation_changed", "Reload the current preparation");
    }
    public async Task<ShipmentDispatchPreparation> ReadPreparationAsync(ActorContext actor, long suborderId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var prepared = await preparations.ReadForDispatchAsync(actor, snapshot, tx, suborderId, token);
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [prepared.Source.Order], Permissions.ShipmentDispatch, token); return View(prepared);
        }, ct);
    }
    private async Task<ShipmentDispatchAdmission> AdmitAsync(ActorContext actor, IClientSessionHandle tx, PreparedShipment prepared,
        ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct) {
        var reader = admissionReader ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
        var admission = await reader.ReadAndFenceAsync(tx, actor, prepared.Source, selected, ct);
        ShipmentDispatchAdmissionPolicy.Require(prepared.Source, selected, admission); ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, clock.UtcNow);
        return admission with { Lots = admission.Lots.ToArray().ToImmutableArray() };
    }
    public async Task<ShipmentDispatchReview> PreviewAsync(ActorContext actor, long suborderId, long preparationId, long preparationRevision,
        long courierId, ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct = default) {
        selected = ShipmentDispatchPolicy.Copy(selected); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var prepared = await preparations.ReadForDispatchAsync(actor, snapshot, tx, suborderId, token); Expected(prepared, preparationId, preparationRevision);
            var target = await couriers.ReadAsync(actor, snapshot, tx, prepared.Source.Order.Resource, courierId, token);
            var effects = OrderSubmissionService.DispatchAllocationFacts(prepared.Source, ShipmentPreparationBson.Lines(prepared.Version["lines"].AsBsonArray), selected);
            var admission = await AdmitAsync(actor, tx, prepared, selected, token);
            var review = B.Review(prepared.Version, courierId, target.GrantVersion, target.ProfileVersion, target.Name, B.Lots(selected), effects, B.Admission(admission));
            await couriers.FenceAsync(actor, snapshot, tx, prepared.Source.Order.Resource, target, token);
            await admissionReader!.CompleteAsync(tx, actor, admission, token);
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [prepared.Source.Order], Permissions.ShipmentDispatch, token);
            couriers.RequireCurrent(actor, snapshot, prepared.Source.Order.Resource, target); ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, clock.UtcNow);
            return new ShipmentDispatchReview(View(prepared), new(courierId), target.Name, selected, review);
        }, ct);
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); InventoryLedger.RequireText(metadata.Reason, 1000); InventoryLedger.RequireText(metadata.CorrelationId, 128);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw ShipmentDispatchAdmissionPolicy.Invalid();
    }
    public async Task<BsonDocument> DispatchAsync(ActorContext actor, long suborderId, long preparationId, long preparationRevision,
        long courierId, ImmutableArray<ShipmentLotSelection> selected, string review, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata); selected = ShipmentDispatchPolicy.Copy(selected);
        if (suborderId <= 0 || preparationId <= 0 || preparationRevision <= 0 || courierId <= 0 || review is null || review.Length != 64 || review.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        await StorageAsync(ct); var id = (await ids.NextAsync(B.Collection, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.Body(preparationId, preparationRevision, courierId, review, selected);
        PreparedShipment? prepared = null; ShipmentCourierTarget? target = null; ShipmentDispatchAdmission? admission = null; AuthorizationSnapshot current = null!;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (snapshot, tx, token) => {
            prepared = null; target = null; admission = null; current = snapshot;
            var source = await orders.ReadForFulfillmentAsync(actor, snapshot, tx, suborderId, Permissions.ShipmentDispatch, token);
            var originals = await Rows(B.Collection).Find(tx, F.Eq("dispatchedBy", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).Limit(2).ToListAsync(token);
            if (originals.Count > 1) throw ShipmentDispatchRecords.Invalid();
            if (originals.Count == 1) _ = await new ShipmentDispatchRecords(database).ReadAsync(tx, originals[0]["_id"].AsInt64, token);
            return new(UnitToko.Modules.Organizations.ScopePolicy.Authorize(actor, snapshot, Permissions.ShipmentDispatch, source.Resource, clock.UtcNow),
                UnitToko.Modules.Organizations.ScopePolicy.RequiredNodes(source.Resource, snapshot.Directory), Permissions.ShipmentDispatch, new(source.Resource));
        }, async (tx, token) => {
            prepared = await preparations.ReadForDispatchAsync(actor, current, tx, suborderId, token); Expected(prepared, preparationId, preparationRevision);
            target = await couriers.ReadAsync(actor, current, tx, prepared.Source.Order.Resource, courierId, token);
            var effects = OrderSubmissionService.DispatchAllocationFacts(prepared.Source, ShipmentPreparationBson.Lines(prepared.Version["lines"].AsBsonArray), selected);
            admission = await AdmitAsync(actor, tx, prepared, selected, token);
            if (review != B.Review(prepared.Version, courierId, target.GrantVersion, target.ProfileVersion, target.Name, B.Lots(selected), effects, B.Admission(admission)))
                throw new PersistenceConflictException("shipment_dispatch_review_changed", "Review the current lots, courier and source authority");
            if (await Rows(B.Collection).Find(tx, F.Eq("preparationVersionId", prepared.Version["_id"])).AnyAsync(token)) throw ShipmentDispatchRecords.Invalid();
            var sequence = await orders.NextShipmentSequenceAsync(tx, actor, prepared.Source.Order.View.OrderId.Value, token);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            var source = prepared.Source.Order; var scope = ShipmentPreparationBson.Scope(source.Resource);
            var numberYear = now.ToOffset(TimeSpan.FromHours(7)).Year;
            var number = await NextNumberAsync(tx, scope["fulfillmentOrganizationId"].AsInt64, scope["channelId"].AsInt64, numberYear, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "fulfillment.shipment" }, { "revision", 1L },
                { "orderId", source.View.OrderId.Value }, { "suborderId", suborderId }, { "orderSequence", sequence }, { "decisionRevision", source.View.Revision },
                { "numberEntity", scope["fulfillmentOrganizationId"] }, { "numberChannel", scope["channelId"] }, { "numberYear", (long)numberYear }, { "number", number },
                { "preparationId", preparationId }, { "preparationVersionId", prepared.Version["_id"] }, { "preparationRevision", preparationRevision },
                { "courierId", courierId }, { "courierName", target.Name }, { "courierGrantVersion", target.GrantVersion }, { "courierProfileVersion", target.ProfileVersion },
                { "assignedUserIds", new BsonArray { courierId } }, { "source", prepared.Version["source"] }, { "scope", scope }, { "lines", prepared.Version["lines"] },
                { "pickedLots", B.Lots(selected) }, { "stockEffects", effects }, { "admission", B.Admission(admission) }, { "review", review },
                { "dispatchedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                { "auditId", auditId }, { "recordedAt", now.UtcDateTime } };
            row.AddRange(scope);
            await Rows(B.Collection).InsertOneAsync(tx, row, cancellationToken: token);
            await orders.ApplyShipmentReservationsAsync(tx, row, token);
            var inventory = new InventoryLedger(database, clock, [new ShipmentDispatchStockSource(database, orders, async finalToken => {
                await couriers.FenceAsync(actor, current, tx, source.Resource, target, finalToken);
                await admissionReader!.CompleteAsync(tx, actor, admission, finalToken);
                await orders.FenceFulfillmentAsync(actor, current, tx, [source], Permissions.ShipmentDispatch, finalToken);
                couriers.RequireCurrent(actor, current, source.Resource, target); ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, clock.UtcNow);
            })]);
            foreach (var pool in effects.Select(effect => effect["allocation"]["poolId"].AsInt64).Distinct().Order())
                await inventory.ApplyAsync(tx, actor, new("fulfillment.shipment", id, "dispatch", pool), 1, new(metadata.Reason, metadata.CorrelationId), token);
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "kind", "fulfillment.shipment.dispatched" }, { "sourceKey", "fulfillment.shipment.dispatched:" + id }, { "payload", B.Result(row) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            await orders.RecordStockSourceTransitionAsync(tx, "shipment", id, token);
            return B.Result(row);
        }, ct, async (snapshot, tx, token) => {
            var receipt = await ReceiptAsync(actor, metadata.IdempotencyKey, tx, token) ?? throw ShipmentDispatchRecords.Invalid();
            var row = await new ShipmentDispatchRecords(database).ReadAsync(tx, receipt["result"]["shipmentId"].AsInt64, token);
            if (row["dispatchedBy"] != actor.UserId || row["requestKey"] != metadata.IdempotencyKey || row["suborderId"] != suborderId ||
                row["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw ShipmentDispatchRecords.Invalid();
            var source = await orders.ReadForFulfillmentAsync(actor, snapshot, tx, suborderId, Permissions.ShipmentDispatch, token);
            if (prepared is not null && target is not null && admission is not null) {
                await preparations.FenceDispatchedPreparationAsync(prepared, tx, token);
                await couriers.FenceAsync(actor, snapshot, tx, source.Resource, target, token);
                await admissionReader!.CompleteAsync(tx, actor, admission, token);
            }
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [source], Permissions.ShipmentDispatch, token);
            if (target is not null && admission is not null) {
                couriers.RequireCurrent(actor, snapshot, source.Resource, target); ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, clock.UtcNow);
            }
        });
    }
    private Task<BsonDocument?> ReceiptAsync(ActorContext actor, string key, IClientSessionHandle tx, CancellationToken ct) =>
        Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", B.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(ct)!;
    // The Surat Jalan sequence is separate from internal IDs, scoped by seller/channel/Jakarta year.
    // Allocation joins dispatch; failed attempts do not issue a number or create a shipment.
    private async Task<long> NextNumberAsync(IClientSessionHandle tx, long entity, long channel, int year, CancellationToken ct) {
        var key = FormattableString.Invariant($"shipment-number.{entity}.{channel}.{year}");
        var row = await Rows("idCounters").Find(tx, F.Eq("_id", key)).SingleOrDefaultAsync(ct);
        if (row is null) {
            await Rows("idCounters").InsertOneAsync(tx, new BsonDocument { { "_id", key }, { "value", 1L } }, cancellationToken: ct); return 1;
        }
        if (!row["value"].IsInt64 || row["value"].AsInt64 is < 1 or long.MaxValue) throw new PersistenceConflictException("shipment_number_unavailable", "Shipment numbering needs operator review");
        if ((await Rows("idCounters").UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("value", 1L), cancellationToken: ct)).ModifiedCount != 1)
            throw new PersistenceConflictException("shipment_number_changed", "Shipment numbering changed; retry the original request");
        return checked(row["value"].AsInt64 + 1);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original dispatch", "shipment-dispatch-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(UnitToko.Modules.Organizations.ScopePolicy.Compile(actor, snapshot, Permissions.ShipmentDispatch, clock.UtcNow));
            var receipt = await ReceiptAsync(actor, key, tx, token);
            if (receipt is null) {
                if (await Rows(B.Collection).Find(tx, F.Eq("dispatchedBy", actor.UserId) & F.Eq("requestKey", key)).AnyAsync(token)) throw ShipmentDispatchRecords.Invalid();
                await orders.FenceFulfillmentAsync(actor, snapshot, tx, [], Permissions.ShipmentDispatch, token); return null;
            }
            var row = await new ShipmentDispatchRecords(database).ReadAsync(tx, receipt["result"]["shipmentId"].AsInt64, token);
            if (row["dispatchedBy"] != actor.UserId || row["requestKey"] != key) throw ShipmentDispatchRecords.Invalid();
            var source = await orders.ReadForFulfillmentAsync(actor, snapshot, tx, row["suborderId"].AsInt64, Permissions.ShipmentDispatch, token);
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [source], Permissions.ShipmentDispatch, token);
            return B.Result(row);
        }, ct);
    }
}

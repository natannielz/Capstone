using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

/// <summary>One shipment's task assignment. Dispatch, documents, visits and acceptance
/// remain immutable owner facts; this store does not assert a physical transfer.</summary>
internal sealed partial class ShipmentCourierRecords(IMongoDatabase database, IClock clock)
{
    private readonly IClock Clock = clock;
    internal const string Anchors = "shipmentCourierAnchors", Heads = "shipmentCourierHeads",
        Events = "shipmentCourierEvents", Bindings = "shipmentCourierBindings", Operation = "fulfillment.courier.handover";
    internal const int MaximumChanges = 1000;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static AuthorizationDeniedException Invalid() => new("shipment_assignment_unavailable");
    internal static PersistenceConflictException Changed() => new("shipment_assignment_changed", "Refresh the current courier assignment");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(row);
    internal static BsonDocument StableHead(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    internal static DateTimeOffset Time(BsonValue value) => ShipmentReceiptRecords.Time(value);
    internal static object Body(long shipmentId, long expectedRevision, long courierId, string review) => new { shipmentId, expectedRevision, courierId, review };
    internal static string Review(BsonDocument head, BsonValue dispatchHash, long courierId, string name, long grantVersion, long profileVersion) => Hash(new() {
        { "assignment", StableHead(head) }, { "dispatchHash", dispatchHash }, { "courierId", courierId }, { "courierName", name },
        { "courierGrantVersion", grantVersion }, { "courierProfileVersion", profileVersion }
    });
    internal static BsonDocument Result(BsonDocument row) => new() { { "eventId", row["_id"] }, { "shipmentId", row["shipmentId"] },
        { "revision", row["revision"] }, { "eventHash", Hash(row) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static ShipmentCourierChange View(BsonDocument row) => new(new(row["_id"].AsInt64), new(row["shipmentId"].AsInt64), row["revision"].AsInt64,
        new(row["previousCourierId"].AsInt64), new(row["courierId"].AsInt64), row["courierName"].AsString, Time(row["recordedAt"]), row["reason"].AsString);
    internal static BsonDocument Anchor(BsonDocument shipment, string origin, BsonArray? legacyBindings = null) => new() { { "_id", shipment["_id"] }, { "schemaVersion", 1 },
        { "origin", origin }, { "dispatchHash", Hash(shipment) }, { "courierId", shipment["courierId"] }, { "courierName", shipment["courierName"] },
        { "scope", shipment["scope"].DeepClone() }, { "recordedAt", shipment["recordedAt"] },
        { "legacyBindingCount", (long)(legacyBindings?.Count ?? 0) }, { "legacyBindingHash", Hash(new BsonDocument("items", legacyBindings ?? [])) } };
    internal static BsonDocument InitialHead(BsonDocument anchor) => new() { { "_id", anchor["_id"] }, { "schemaVersion", 1 }, { "anchorHash", Hash(anchor) },
        { "revision", 1L }, { "courierId", anchor["courierId"] }, { "courierName", anchor["courierName"] }, { "effectiveAt", anchor["recordedAt"] },
        { "eventId", BsonNull.Value }, { "eventHash", Hash(anchor) }, { "scope", anchor["scope"].DeepClone() },
        { "assignedUserIds", new BsonArray { anchor["courierId"] } }, { "fence", 0L } };
    internal static BsonDocument Advance(BsonDocument previous, BsonDocument change) {
        var next = previous.DeepClone().AsBsonDocument;
        next["revision"] = change["revision"]; next["courierId"] = change["courierId"]; next["courierName"] = change["courierName"];
        next["effectiveAt"] = change["recordedAt"]; next["eventId"] = change["_id"]; next["eventHash"] = Hash(change);
        next["assignedUserIds"] = new BsonArray { change["courierId"] }; return next;
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in ShipmentCourierSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!AccountContactStorage.SafeDefinition(definition, ShipmentCourierSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, ShipmentCourierSchemas.Keys(name)) ||
                ShipmentCourierSchemas.Keys(name).Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw Invalid();
        }
    }
    internal async Task InitializeAsync(IClientSessionHandle tx, BsonDocument shipment, CancellationToken ct) {
        await RequireStorageAsync(database, ct);
        var anchor = Anchor(shipment, "dispatch");
        await Rows(Anchors).InsertOneAsync(tx, anchor, cancellationToken: ct);
        await Rows(Heads).InsertOneAsync(tx, InitialHead(anchor), cancellationToken: ct);
    }
    internal async Task<Capture> ReadAsync(IClientSessionHandle tx, BsonDocument shipment, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Invalid();
        var at = Clock.UtcNow;
        var anchor = await Rows(Anchors).Find(tx, F.Eq("_id", shipment["_id"]) & ShipmentCourierSchemas.Validator(Anchors)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var legacy = await Rows(Bindings).Find(tx, F.Eq("shipmentId", shipment["_id"]) & F.Eq("auditId", BsonNull.Value)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var seals = new BsonArray(legacy.Select(row => new BsonDocument { { "key", row["_id"] }, { "sourceHash", row["sourceHash"] } }));
        if (anchor != Anchor(shipment, anchor["origin"].AsString, seals) || at < Time(anchor["recordedAt"]) ||
            anchor["origin"] == "dispatch" && legacy.Count != 0) throw Invalid();
        var head = await Rows(Heads).Find(tx, F.Eq("_id", shipment["_id"]) & ShipmentCourierSchemas.Validator(Heads)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var events = await Rows(Events).Find(tx, F.Eq("shipmentId", shipment["_id"])).Sort(new BsonDocument("revision", 1)).Limit(MaximumChanges + 1).ToListAsync(ct);
        if (events.Count > MaximumChanges) throw Invalid();
        var expected = InitialHead(anchor); var retained = new List<(string Name, BsonDocument Row)> { ("shipments", shipment.DeepClone().AsBsonDocument), (Anchors, anchor) };
        retained.AddRange(legacy.Select(row => (Bindings, row)));
        foreach (var row in events) {
            if (!await Rows(Events).Find(tx, F.Eq("_id", row["_id"]) & ShipmentCourierSchemas.Validator(Events)).AnyAsync(ct) ||
                row["anchorHash"] != Hash(anchor) || row["dispatchHash"] != anchor["dispatchHash"] || row["revision"] != expected["revision"].AsInt64 + 1 ||
                row["previousHash"] != expected["eventHash"] || row["previousCourierId"] != expected["courierId"] || row["courierId"] == expected["courierId"] ||
                row["review"] != Review(expected, anchor["dispatchHash"], row["courierId"].AsInt64, row["courierName"].AsString, row["courierGrantVersion"].AsInt64, row["courierProfileVersion"].AsInt64) ||
                Time(row["recordedAt"]) < Time(expected["effectiveAt"]) || Time(row["recordedAt"]) > at ||
                row["bodyHash"] != AuthorizationStore.CommandHash(Body(shipment["_id"].AsInt64, row["revision"].AsInt64 - 1, row["courierId"].AsInt64, row["review"].AsString), row["reason"].AsString)) throw Invalid();
            var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", Operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
            var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", Operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct);
            if (audit is null || audit != Audit(row) || receipt is null || receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
                !receipt["createdAt"].IsBsonDateTime || Time(receipt["createdAt"]) > Time(row["recordedAt"]) ||
                receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != Result(row) || receipt.Contains(PersonApprovalReceipt.Field) ||
                outbox is null || outbox["schemaVersion"] != 1 || !outbox["_id"].IsObjectId || outbox["actorId"] != row["actorId"] || outbox["requestKey"] != row["requestKey"] ||
                outbox["kind"] != Operation || outbox["payload"] != Result(row) || outbox["createdAt"] != row["recordedAt"]) throw Invalid();
            retained.Add((Events, row)); retained.Add(("auditEvents", audit)); retained.Add(("idempotencyRecords", receipt)); retained.Add(("outboxEvents", outbox));
            expected = Advance(expected, row);
        }
        if (StableHead(head) != StableHead(expected)) throw Invalid();
        foreach (var (name, field, result) in new[] { ("auditEvents", "operation", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") })
            if (await Rows(name).CountDocumentsAsync(tx, F.Eq(field, Operation) & F.Eq(result + ".shipmentId", shipment["_id"]), cancellationToken: ct) != events.Count) throw Invalid();
        return new(this, tx, shipment.DeepClone().AsBsonDocument, anchor, head, events, retained, at);
    }
    internal sealed class Capture(ShipmentCourierRecords owner, IClientSessionHandle tx, BsonDocument shipment, BsonDocument anchor,
        BsonDocument head, IReadOnlyList<BsonDocument> events, IReadOnlyList<(string Name, BsonDocument Row)> retained, DateTimeOffset capturedAt) {
        private readonly long? transaction = tx.WrappedCoreSession.CurrentTransaction?.TransactionNumber;
        private readonly Dictionary<string, (string Kind, BsonDocument Binding, BsonDocument Source, BsonDocument? Audit)> evidence = new(StringComparer.Ordinal);
        internal BsonDocument Shipment { get; } = shipment;
        internal BsonDocument Anchor { get; } = anchor;
        internal BsonDocument Head { get; private set; } = head;
        internal IReadOnlyList<BsonDocument> Events { get; } = events;
        internal long Revision => Head["revision"].AsInt64;
        internal long CourierId => Head["courierId"].AsInt64;
        internal ShipmentCurrentCourier View => new(Revision, new(CourierId), Head["courierName"].AsString, Time(Head["effectiveAt"]));
        internal void Require(IClientSessionHandle caller) {
            if (!ReferenceEquals(caller, tx) || !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction?.TransactionNumber != transaction || owner.Clock.UtcNow < capturedAt) throw Invalid();
        }
        internal void RetainBinding(IClientSessionHandle caller, Capture authenticated, string kind, BsonDocument binding, BsonDocument source, BsonDocument? audit) {
            Require(caller); authenticated.Require(caller);
            if (Shipment != authenticated.Shipment || Anchor != authenticated.Anchor || StableHead(Head) != StableHead(authenticated.Head)) throw Invalid();
            var value = (kind, binding.DeepClone().AsBsonDocument, StableSource(kind, source), audit?.DeepClone().AsBsonDocument);
            var key = binding["_id"].AsString;
            if (evidence.TryGetValue(key, out var prior) && (prior.Kind != value.kind || prior.Binding != value.Item2 || prior.Source != value.Item3 || prior.Audit != value.Item4)) throw Invalid();
            evidence[key] = value;
        }
        internal async Task CompleteAsync(IClientSessionHandle caller, CancellationToken ct) {
            Require(caller);
            foreach (var (name, row) in retained)
                if (await owner.Rows(name).Find(tx, F.Eq("_id", row["_id"])).SingleOrDefaultAsync(ct) != row) throw Invalid();
            foreach (var (kind, binding, source, audit) in evidence.Values) {
                var current = await owner.Rows(SourceTable(kind)).Find(tx, F.Eq("_id", source["_id"])).SingleOrDefaultAsync(ct);
                if (current is null || StableSource(kind, current) != source ||
                    await owner.Rows(Bindings).Find(tx, F.Eq("_id", binding["_id"])).SingleOrDefaultAsync(ct) != binding ||
                    audit is not null && await owner.Rows("auditEvents").Find(tx, F.Eq("_id", audit["_id"])).SingleOrDefaultAsync(ct) != audit) throw Invalid();
            }
            if (await owner.Rows(EventsCollection).CountDocumentsAsync(tx, F.Eq("shipmentId", Shipment["_id"]), cancellationToken: ct) != Events.Count) throw Invalid();
            if (await owner.Rows(Bindings).CountDocumentsAsync(tx, F.Eq("shipmentId", Shipment["_id"]) & F.Eq("auditId", BsonNull.Value), cancellationToken: ct) != Anchor["legacyBindingCount"].AsInt64) throw Invalid();
            foreach (var (name, field, result) in new[] { ("auditEvents", "operation", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") })
                if (await owner.Rows(name).CountDocumentsAsync(tx, F.Eq(field, Operation) & F.Eq(result + ".shipmentId", Shipment["_id"]), cancellationToken: ct) != Events.Count) throw Invalid();
            if (Head["fence"].AsInt64 >= long.MaxValue - 1 || (await owner.Rows(Heads).UpdateOneAsync(tx, Head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
            Head = Head.DeepClone().AsBsonDocument; Head["fence"] = Head["fence"].AsInt64 + 1;
            Require(caller); ct.ThrowIfCancellationRequested();
        }
        private const string EventsCollection = ShipmentCourierRecords.Events;
        internal (long CourierId, string Hash, DateTimeOffset At, DateTimeOffset? Until) Historical(long revision) {
            if (revision < 1 || revision > Revision) throw Invalid();
            var item = revision == 1 ? Anchor : Events[checked((int)revision - 2)];
            var until = revision == Revision ? (DateTimeOffset?)null : Time(Events[checked((int)revision - 1)]["recordedAt"]);
            return (item["courierId"].AsInt64, Hash(item), Time(item["recordedAt"]), until);
        }
    }
}

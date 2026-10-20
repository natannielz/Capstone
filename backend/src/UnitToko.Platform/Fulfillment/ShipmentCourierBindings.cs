using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal sealed partial class ShipmentCourierRecords
{
    internal static string BindingOperation(string kind) => "fulfillment.courier.binding." + kind;
    internal static string BindingKey(string kind, BsonDocument row) => kind + ":" + row["_id"].AsInt64;
    internal static string SourceTable(string kind) => kind switch {
        "attempt" => DeliveryAttemptRecords.Attempts, "challenge" => ShipmentReceiptRecords.Challenges,
        "receipt" => ShipmentReceiptRecords.Receipts, _ => throw Invalid()
    };
    internal static BsonDocument StableSource(string kind, BsonDocument row) {
        _ = SourceTable(kind); var copy = row.DeepClone().AsBsonDocument;
        if (kind == "challenge") foreach (var field in new[] { "failedAttempts", "consumedReceiptId", "consumedAt" }) copy.Remove(field);
        return copy;
    }
    internal static BsonDocument LegacySeal(string kind, BsonDocument row) => new() { { "key", BindingKey(kind, row) }, { "sourceHash", Hash(StableSource(kind, row)) } };
    internal static BsonDocument Binding(string kind, BsonDocument row, BsonDocument anchor, long revision, string assignmentHash, long? auditId) => new() {
        { "_id", BindingKey(kind, row) }, { "schemaVersion", 1 }, { "kind", kind }, { "recordId", row["_id"] }, { "shipmentId", row["shipmentId"] },
        { "anchorHash", Hash(anchor) }, { "assignmentRevision", revision }, { "assignmentHash", assignmentHash },
        { "sourceHash", Hash(StableSource(kind, row)) }, { "auditId", auditId is { } id ? new BsonInt64(id) : BsonNull.Value }
    };
    internal static BsonDocument BindingAudit(BsonDocument binding, BsonDocument row) => new() {
        { "_id", binding["auditId"] }, { "schemaVersion", 1 }, { "kind", BindingOperation(binding["kind"].AsString) },
        { "operation", BindingOperation(binding["kind"].AsString) }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "sourceAuditId", row["auditId"] }, { "binding", binding.DeepClone() }
    };
    internal async Task BindAsync(IClientSessionHandle tx, ShipmentDocumentSource source, string kind, BsonDocument row, CancellationToken ct) {
        var capture = source.Assignment ?? throw Invalid(); capture.Require(tx);
        if (row["shipmentId"] != capture.Shipment["_id"] || row["courierId"] != capture.CourierId || Time(row["recordedAt"]) < capture.View.EffectiveAt) throw Invalid();
        var audit = (await new IdAllocator(database).NextAsync("auditEvents", ct)).Value;
        var binding = Binding(kind, row, capture.Anchor, capture.Revision, capture.Head["eventHash"].AsString, audit);
        await Rows(Bindings).InsertOneAsync(tx, binding, cancellationToken: ct);
        await Rows("auditEvents").InsertOneAsync(tx, BindingAudit(binding, row), cancellationToken: ct);
    }
    // Historical evidence always resolves its own retained assignment. It does not borrow
    // today's courier or silently treat a missing sidecar as a legacy record.
    internal async Task<long> RequireBindingAsync(IClientSessionHandle tx, ShipmentDocumentSource source, string kind, BsonDocument row, CancellationToken ct) {
        var shipment = await Rows("shipments").Find(tx, F.Eq("_id", source.Snapshot["shipmentId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
        if (Hash(shipment) != source.Snapshot["dispatchHash"]) throw Invalid();
        var capture = await ReadAsync(tx, shipment, ct);
        var binding = await Rows(Bindings).Find(tx, F.Eq("_id", BindingKey(kind, row)) & ShipmentCourierSchemas.Validator(Bindings)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var revision = binding["assignmentRevision"].AsInt64; var historical = capture.Historical(revision);
        var auditId = binding["auditId"].IsBsonNull ? (long?)null : binding["auditId"].AsInt64;
        if (binding != Binding(kind, row, capture.Anchor, revision, historical.Hash, auditId) || row["shipmentId"] != shipment["_id"] ||
            row["courierId"] != historical.CourierId || Time(row["recordedAt"]) < historical.At || historical.Until is { } until && Time(row["recordedAt"]) > until ||
            kind == "attempt" && Time(row["occurredAt"]) < historical.At) throw Invalid();
        BsonDocument? audit = null;
        if (auditId is { } id) {
            audit = await Rows("auditEvents").Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct);
            if (audit != BindingAudit(binding, row)) throw Invalid();
        } else if (revision != 1 || capture.Anchor["origin"] != "migration120") throw Invalid();
        source.Assignment?.RetainBinding(tx, capture, kind, binding, row, audit);
        return revision;
    }
    internal async Task RequireCurrentChallengeAsync(IClientSessionHandle tx, ShipmentDocumentSource source, BsonDocument challenge, CancellationToken ct) {
        var revision = await RequireBindingAsync(tx, source, "challenge", challenge, ct);
        var current = source.Assignment ?? throw Invalid(); current.Require(tx);
        if (revision != current.Revision || challenge["courierId"] != current.CourierId)
            throw new AuthorizationDeniedException("receipt_challenge_reassigned");
    }
}

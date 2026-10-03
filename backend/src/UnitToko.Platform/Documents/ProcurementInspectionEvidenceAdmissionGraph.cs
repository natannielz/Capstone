using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// Forward preflight retains the accepted admission transition graph for catalog, payment, refund, procurement image and inspection video targets.
// The caller validates every predecessor row first; no current actor or provider is fabricated.
internal static class ProcurementInspectionEvidenceAdmissionGraph
{
    private static readonly string[] IdCollections = ["evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts"];
    internal static Task ValidateAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) =>
        ValidateAsync(database, lease, ReserveOperation, CompleteOperation, ct);
    internal static Task ValidateSupplierDocumentAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) =>
        ValidateAsync(database, lease,
            row => row["target"]["kind"] == "SupplierDocument" ? "evidence.supplier-document-image.reserve" : ReserveOperation(row),
            row => row["target"]["kind"] == "SupplierDocument" ? "evidence.supplier-document-image.complete" : CompleteOperation(row), ct);
    private static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease,
        Func<BsonDocument, string> reserveOperation, Func<BsonDocument, string> completeOperation, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument> Get(string name, BsonValue id)
        {
            var row = await Rows(name).Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
            if (row is null) throw Bad(name);
            return row;
        }
        foreach (var collection in IdCollections)
            await EvidenceMigration.EachAsync(Rows(collection), new BsonDocument(), async row =>
            {
                foreach (var other in IdCollections.Where(name => name != collection))
                    if (await Rows(other).Find(new BsonDocument("_id", row["_id"])).Limit(1).AnyAsync(ct)) throw Bad(collection);
                var reservation = collection == "evidenceUploadReservations" ? row : collection == "evidenceLineages"
                    ? await Get("evidenceUploadReservations", (await Get("documentVersions", row["firstVersionRowId"]))["reservationId"])
                    : await Get("evidenceUploadReservations", row["reservationId"]);
                var expected = collection switch { "evidenceLineages" => "lineageId", "documentVersions" => "versionRowId", "evidenceUploadAttempts" => "attemptId", _ => "_id" };
                if (row["_id"] != reservation[expected]) throw Bad(collection);
            }, lease, ct);

        await EvidenceMigration.EachAsync(Rows("evidenceUploadReservations"), new BsonDocument(), async reservation =>
        {
            var r = reservation;
            var l = await Get("evidenceLineages", r["lineageId"]);
            var a = await Get("evidenceUploadAttempts", r["attemptId"]);
            var v = await Get("documentVersions", r["versionRowId"]);
            var p = await Get(r["purpose"] == "ProcurementUnboxingVideo" ? "evidenceVideoStorePolicies" : "evidenceStorePolicies", r["policyId"]);
            if (l["firstVersionRowId"] != v["_id"] || a["reservationId"] != r["_id"] || v["reservationId"] != r["_id"] ||
                v["attemptId"] != a["_id"] || v["lineageId"] != l["_id"] ||
                new[] { "target", "purpose", "owner", "uploader" }.Any(k => !Same(l[k], r[k])) ||
                p["storeId"] != a["storeId"] || a["storeId"] != v["storeId"] || a["generation"] != v["generation"] ||
                p["policyVersion"].AsInt64 < r["policyVersion"].AsInt64 ||
                a["createdAt"] != r["createdAt"] || v["createdAt"] != r["createdAt"] || a["revision"] != r["revision"] || v["revision"].AsInt64 < r["revision"].AsInt64 || l["createdAt"] != r["createdAt"] ||
                r["state"] != v["state"] || (r["state"] == "quarantined") != (a["state"] == "quarantined") ||
                a["completionAuditId"] != r["completionAuditId"] ||
                new[] { "contentRevision", "length", "sha256" }.Any(k => a[k] != v[k])) throw Bad("reservation bindings");
            if (a["length"].IsInt64 && a["length"].AsInt64 > r["admittedLimits"]["maximumContentBytes"].AsInt64) throw Bad("content limit");
            var created = Milliseconds(r["createdAt"]); var expires = Milliseconds(r["expiresAt"]);
            if (expires - created != r["admittedLimits"]["reservationDurationMs"].AsInt64 || v["lifecycleRevision"] != (r["state"] == "quarantined" ? 2L : 1L)) throw Bad("lifecycle or expiry");
            if (a["state"] != "fresh")
            {
                var writerUntil = Milliseconds(a["writerUntil"]);
                if (writerUntil <= created || writerUntil > expires) throw Bad("writer window");
                if (a["claimKind"] == "stage" && (a["claimUntil"] != a["writerUntil"] || a["claimRequestId"] != a["writerRequestId"])) throw Bad("stage writer binding");
                if (!a["claimUntil"].IsBsonNull && (Milliseconds(a["claimUntil"]) > expires ||
                    a["claimRequestId"] == a["writerRequestId"] && a["claimUntil"] != a["writerUntil"])) throw Bad("publication writer binding");
            }
            var reserveAudit = await Get("auditEvents", r["reserveAuditId"]);
            RequireAudit(reserveAudit, r, "evidence.reserve");
            if (reserveAudit["bodyHash"] != r["bodyHash"] || reserveAudit["evidence"]["from"]["reservation"] != 0L ||
                reserveAudit["evidence"]["to"]["reservation"] != 1L) throw Bad("reserve audit");
            var receipt = await OneAsync(Rows("idempotencyRecords"), new BsonDocument {
                { "actorId", r["uploader"]["userId"] }, { "operation", reserveOperation(r) }, { "key", r["key"] } }, ct);
            RequireReceipt(receipt, r, v, r["reserveAuditId"], false, reserveOperation, completeOperation);

            if (r["state"] == "quarantined")
            {
                var audit = await Get("auditEvents", r["completionAuditId"]);
                RequireAudit(audit, r, "evidence.quarantine");
                if (audit["bodyHash"] != CompletionHash(r, v) || v["quarantinedAt"] != audit["createdAt"] ||
                    audit["evidence"]["to"]["reservation"] != r["revision"]) throw Bad("completion audit");
                var completeReceipt = await OneAsync(Rows("idempotencyRecords"), new BsonDocument {
                    { "actorId", r["uploader"]["userId"] }, { "operation", completeOperation(r) }, { "key", CompletionKey(r) } }, ct);
                RequireReceipt(completeReceipt, r, v, r["completionAuditId"], true, reserveOperation, completeOperation);
                var message = await OneAsync(Rows("outboxEvents"), new BsonDocument("sourceKey", SourceKey(v)), ct);
                if (message["kind"] != (r["purpose"] == "ProcurementUnboxingVideo" ? "evidence.video-validation.requested" : "evidence.validation.requested") || !Same(message["payload"], Payload(r, v))) throw Bad("completion outbox");
            }
            else
            {
                if (await Rows("idempotencyRecords").Find(new BsonDocument { { "operation", completeOperation(r) }, { "evidenceAdmission.reservationId", r["_id"] } }).Limit(1).AnyAsync(ct) ||
                    await Rows("outboxEvents").Find(new BsonDocument("sourceKey", SourceKey(v))).Limit(1).AnyAsync(ct)) throw Bad("premature completion");
            }

            long revision = 0, lifecycle = 0, claimEpoch = 0;
            var state = "none";
            await EvidenceMigration.EachAsync(Rows("auditEvents"), new BsonDocument {
                { "evidence.reservationId", r["_id"] }, { "kind", new BsonDocument("$in", new BsonArray { "evidence.reserve", "evidence.claim", "evidence.freeze", "evidence.interrupted", "evidence.quarantine" }) } },
                audit =>
                {
                    var kind = audit["kind"].AsString;
                    RequireAudit(audit, r, kind);
                    var from = audit["evidence"]["from"]; var to = audit["evidence"]["to"];
                    if (from["reservation"] != revision || from["attempt"] != revision || from["lifecycle"] != lifecycle ||
                        to["reservation"] != checked(revision + 1) || to["attempt"] != checked(revision + 1)) throw Bad("audit transition chain");
                    var next = kind switch {
                        "evidence.reserve" when state == "none" => "fresh",
                        "evidence.claim" when state == "fresh" => "writing",
                        "evidence.claim" when state is "frozen" or "publication" => "publication",
                        "evidence.freeze" when state == "writing" => "frozen",
                        "evidence.interrupted" when state == "writing" => "interrupted",
                        "evidence.quarantine" when state == "publication" => "quarantined",
                        _ => throw Bad("audit transition")
                    };
                    if (kind == "evidence.claim") claimEpoch = checked(claimEpoch + 1);
                    lifecycle = next == "quarantined" ? 2 : 1;
                    if (to["lifecycle"] != lifecycle) throw Bad("audit lifecycle");
                    revision++; state = next; return Task.CompletedTask;
                }, lease, ct, new() { { "evidence.to.reservation", 1 }, { "_id", 1 } });
            var durableState = state is "frozen" or "publication" ? "publish_pending" : state;
            BsonValue claimKind = state switch { "writing" => new BsonString("stage"), "publication" => new BsonString("publication"), _ => BsonNull.Value };
            if (revision != r["revision"].AsInt64 || durableState != a["state"].AsString || claimEpoch != a["claimEpoch"].AsInt64 ||
                a["claimKind"] != claimKind) throw Bad("missing audit transition or claim binding");
        }, lease, ct);

        await EvidenceMigration.EachAsync(Rows("auditEvents"), EvidenceSchemas.Condition("auditEvents")!, async audit =>
        {
            var r = await Get("evidenceUploadReservations", audit["evidence"]["reservationId"]);
            RequireAudit(audit, r, audit["kind"].AsString);
            if (audit["kind"] == "evidence.status")
            {
                if (!Same(audit["evidence"]["from"], audit["evidence"]["to"]) ||
                    audit["evidence"]["to"]["reservation"].AsInt64 > r["revision"].AsInt64 || !audit["bodyHash"].IsBsonNull) throw Bad("status audit");
            }
        }, lease, ct);
        await EvidenceMigration.EachAsync(Rows("idempotencyRecords"), EvidenceSchemas.Condition("idempotencyRecords")!, async receipt =>
        {
            var r = await Get("evidenceUploadReservations", receipt["evidenceAdmission"]["reservationId"]);
            var complete = receipt["operation"] == completeOperation(r);
            RequireReceipt(receipt, r, await Get("documentVersions", r["versionRowId"]), r[complete ? "completionAuditId" : "reserveAuditId"], complete, reserveOperation, completeOperation);
        }, lease, ct);
        await EvidenceMigration.EachAsync(Rows("outboxEvents"), EvidenceSchemas.Condition("outboxEvents")!, async message =>
        {
            var r = await Get("evidenceUploadReservations", message["payload"]["reservationId"]);
            var v = await Get("documentVersions", r["versionRowId"]);
            if (r["state"] != "quarantined" || message["sourceKey"] != SourceKey(v) || !Same(message["payload"], Payload(r, v))) throw Bad("outbox binding");
        }, lease, ct);

        foreach (var policyCollection in new[] { "evidenceStorePolicies", "evidenceVideoStorePolicies" })
        await EvidenceMigration.EachAsync(Rows(policyCollection), new BsonDocument(), async policy =>
        {
            long bytes = 0, count = 0;
            var purpose = policyCollection == "evidenceVideoStorePolicies" ? (BsonValue)"ProcurementUnboxingVideo" : new BsonDocument("$ne", "ProcurementUnboxingVideo");
            await EvidenceMigration.EachAsync(Rows("evidenceUploadReservations"), new BsonDocument { { "policyId", policy["_id"] }, { "purpose", purpose } }, row =>
            {
                try { bytes = checked(bytes + row["chargedBytes"].AsInt64); count = checked(count + 1); }
                catch (OverflowException) { throw Bad("quota overflow"); }
                return Task.CompletedTask;
            }, lease, ct);
            if (policy["chargedBytes"] != bytes || policy["chargedReservations"] != count ||
                bytes > policy["maximumHeldBytes"].AsInt64 || count > policy["maximumHeldReservations"].AsInt64) throw Bad("retained quota");
        }, lease, ct);
    }

    private static async Task<BsonDocument> OneAsync(IMongoCollection<BsonDocument> rows, BsonDocument filter, CancellationToken ct)
    {
        using var cursor = await rows.FindAsync(filter, new FindOptions<BsonDocument> { Limit = 2, BatchSize = 2 }, ct);
        BsonDocument? result = null;
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) { if (result is not null) throw Bad("duplicate relation"); result = row; }
        return result ?? throw Bad("missing relation");
    }
    private static void RequireAudit(BsonDocument audit, BsonDocument r, string kind)
    {
        if (audit["kind"] != kind ||
            kind != "evidence.status" && audit["actorId"] != r["uploader"]["userId"]) throw Bad("audit provenance");
        var evidence = audit["evidence"];
        foreach (var field in new[] { "attemptId", "versionRowId", "lineageId", "target", "purpose", "owner" })
            if (!Same(evidence[field], r[field])) throw Bad("audit binding");
        if (evidence["reservationId"] != r["_id"]) throw Bad("audit reservation");
        if (kind is not ("evidence.reserve" or "evidence.quarantine") && !audit["bodyHash"].IsBsonNull) throw Bad("audit fingerprint");
    }
    private static void RequireReceipt(BsonDocument receipt, BsonDocument r, BsonDocument v, BsonValue auditId, bool completed,
        Func<BsonDocument, string> reserveOperation, Func<BsonDocument, string> completeOperation)
    {
        if (receipt["actorId"] != r["uploader"]["userId"] ||
            receipt["operation"] != (completed ? completeOperation(r) : reserveOperation(r)) ||
            receipt["key"] != (completed ? CompletionKey(r) : r["key"].AsString) ||
            receipt["bodyHash"] != (completed ? CompletionHash(r, v) : r["bodyHash"].AsString) ||
            receipt["evidenceAdmission"]["reservationId"] != r["_id"] || receipt["evidenceAdmission"]["auditId"] != auditId ||
            !Same(receipt["result"], Result(r, v, completed)) || completed && r["state"] != "quarantined") throw Bad("receipt provenance");
    }
    private static BsonDocument Result(BsonDocument r, BsonDocument v, bool completed) => new() {
        { "reservationId", r["_id"] }, { "lineageId", r["lineageId"] }, { "versionRowId", r["versionRowId"] },
        { "attemptId", r["attemptId"] }, { "contentVersion", 1L }, { "revision", completed ? r["revision"] : new BsonInt64(1) },
        { "expiresAt", r["expiresAt"] }, { "sha256", completed ? v["sha256"] : BsonNull.Value }, { "lifecycleRevision", completed ? v["lifecycleRevision"] : new BsonInt64(1) } };
    private static BsonDocument Payload(BsonDocument r, BsonDocument v) => new() {
        { "lineageId", r["lineageId"] }, { "versionRowId", r["versionRowId"] }, { "reservationId", r["_id"] }, { "attemptId", r["attemptId"] },
        { "contentVersion", 1L }, { "contentRevision", 1L }, { "sha256", v["sha256"] }, { "lifecycleRevision", v["lifecycleRevision"] } };
    private static string ReserveOperation(BsonDocument reservation) => reservation["target"]["kind"].AsString switch {
        "CatalogFamily" or "CatalogSku" => "evidence.catalog-image.reserve", "PaymentDeclaration" => "evidence.payment-image.reserve", "RefundPayout" => "evidence.refund-payout-image.reserve", "ProcurementDisbursement" => "evidence.procurement-disbursement-image.reserve", "ProcurementInspection" => "evidence.procurement-unboxing-video.reserve",
        _ => throw Bad("predecessor target") };
    private static string CompleteOperation(BsonDocument reservation) => reservation["target"]["kind"].AsString switch {
        "CatalogFamily" or "CatalogSku" => "evidence.catalog-image.complete", "PaymentDeclaration" => "evidence.payment-image.complete", "RefundPayout" => "evidence.refund-payout-image.complete", "ProcurementDisbursement" => "evidence.procurement-disbursement-image.complete", "ProcurementInspection" => "evidence.procurement-unboxing-video.complete",
        _ => throw Bad("predecessor target") };
    private static string CompletionKey(BsonDocument r) => $"{r["_id"].AsInt64}:{r["attemptId"].AsInt64}:1";
    private static string CompletionHash(BsonDocument r, BsonDocument v) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompletionKey(r) + ":" + v["sha256"].AsString))).ToLowerInvariant();
    private static string SourceKey(BsonDocument v) => $"evidence.quarantined:{v["_id"].AsInt64}:1";
    private static long Milliseconds(BsonValue value) => value.AsBsonDateTime.MillisecondsSinceEpoch;
    private static bool Same(BsonValue left, BsonValue right) => CatalogSchemas.SemanticEquals(left, right);
    private static InvalidOperationException Bad(string relation) => EvidenceMigration.Invalid("admission graph", relation);
}

using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementInspectionSchemas
{
    internal static readonly string[] Collections = [B.Drafts, B.Submissions, B.Heads];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Count(int minimum = 0) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", 1000000 } };
    private static BsonDocument Total => S.Number(0, long.MaxValue - 2);
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Line() => S.Object(new() { { "requestSkuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1, long.MaxValue - 2) },
        { "declaredPackages", Count() }, { "receivedPackages", Count() }, { "damagedPackages", Count() }, { "expiredPackages", Count() }, { "openedPackages", Count() },
        { "lotCode", Nullable(S.Text(128)) }, { "expiryDate", Nullable(Day) }, { "putawayReference", Nullable(S.Text(256)) }, { "discrepancy", Nullable(S.Text(1000)) } });
    internal static BsonDocument Input() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() }, { "noticeId", S.Number(1) }, { "intakeId", S.Number(1) },
        { "inspectionDate", Day }, { "inspectionReference", S.Text(128) }, { "inspectedContainers", Count(1) }, { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", Line() } } },
        { "discrepancy", Nullable(S.Text(1000)) } });
    internal static BsonDocument Capacity() => S.Object(new() { { "version", Total }, { "receivedContainers", S.Number(1, 1000000) }, { "inspectedContainers", S.Number(0, 1000000) }, { "remainingContainers", S.Number(0, 1000000) } });
    internal static BsonDocument Source() => S.Object(new() {
        { "notice", ProcurementReceivingSchemas.Validator(C.Notices)["$and"][0]["$jsonSchema"].DeepClone() },
        { "intake", ProcurementReceivingSchemas.Validator(C.Intakes)["$and"][0]["$jsonSchema"].DeepClone() } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Drafts => [new() { { "intakeId", 1 }, { "input.inspectionReference", 1 } }, new("auditId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Submissions => [new("draftId", 1), new() { { "intakeId", 1 }, { "sequence", 1 } }, new("pinId", 1), new("auditId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Heads => [new("_id", 1)], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "noticeId", S.Number(1) }, { "intakeId", S.Number(1) }, { "location", ProcurementRequestSchemas.Location() } };
        var checks = new BsonArray();
        if (name == B.Heads) {
            fields.AddRange(new BsonDocument { { "intakeHash", Hash }, { "version", Total }, { "receivedContainers", S.Number(1, 1000000) }, { "inspectedContainers", S.Number(0, 1000000) },
                { "remainingContainers", S.Number(0, 1000000) }, { "lastInspectionId", Nullable(S.Number(1)) }, { "lastHash", Nullable(Hash) }, { "createdAt", Date }, { "updatedAt", Date }, { "fence", Total } });
            checks.Add(Op("$eq", "$_id", "$intakeId")); checks.Add(Op("$gte", "$updatedAt", "$createdAt")); Totals(checks, "$");
            foreach (var field in new[] { "lastInspectionId", "lastHash" }) checks.Add(Op("$eq", Op("$eq", "$version", 0L), Op("$eq", "$" + field, BsonNull.Value)));
        } else {
            fields.AddRange(new BsonDocument { { "draftId", S.Number(1) }, { "version", S.Number(1, 1) }, { "operation", S.Choice(name == B.Drafts ? B.DraftOperation : B.SubmitOperation) },
                { "input", Input() }, { "source", Source() }, { "binding", Hash }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Object(new() {
                    { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } }) } } },
                { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields);
            foreach (var field in new[] { "noticeId", "intakeId", "location" }) checks.Add(Op("$eq", "$" + field, "$input." + field));
            checks.Add(Op("$eq", "$noticeId", "$source.notice._id")); checks.Add(Op("$eq", "$intakeId", "$source.intake._id")); checks.Add(Op("$eq", "$noticeId", "$source.intake.noticeId"));
            checks.Add(Op("$eq", "$location", "$source.notice.location")); checks.Add(Op("$eq", "$location", "$source.intake.location"));
            checks.Add(Op("$eq", "$source.intake.noticeHash", "$source.notice.snapshotHash")); checks.Add(Op("$lte", "$source.intake.recordedAt", "$recordedAt"));
            checks.Add(Op("$eq", "$actorId", new BsonDocument("$arrayElemAt", new BsonArray { "$people.userId", 0 })));
            checks.Add(Op("$gte", "$input.inspectionDate", "$source.intake.input.receivedDate")); checks.Add(Op("$lte", "$input.inspectionDate", new BsonDocument("$dateToString", new BsonDocument {
                { "date", "$recordedAt" }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" } })));
            checks.Add(Op("$ne", new BsonDocument("$dateFromString", new BsonDocument { { "dateString", "$input.inspectionDate" }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value));
            checks.Add(new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument { { "input", "$input.lines" }, { "as", "line" }, { "in", Op("$and",
                Op("$gt", Op("$add", "$$line.declaredPackages", "$$line.receivedPackages"), 0), Op("$lte", "$$line.damagedPackages", "$$line.receivedPackages"),
                Op("$lte", "$$line.expiredPackages", "$$line.receivedPackages"), Op("$lte", "$$line.openedPackages", "$$line.receivedPackages"),
                Op("$or", Op("$and", Op("$eq", "$$line.declaredPackages", "$$line.receivedPackages"), Op("$eq", "$$line.damagedPackages", 0), Op("$eq", "$$line.expiredPackages", 0), Op("$eq", "$$line.openedPackages", 0)), Op("$ne", "$$line.discrepancy", BsonNull.Value))) } }) }));
            if (name == B.Drafts) {
                fields.AddRange(new BsonDocument { { "capacity", Capacity() }, { "submittedInspectionId", Nullable(S.Number(1)) }, { "fence", Total } });
                checks.Add(Op("$eq", "$_id", "$draftId")); Totals(checks, "$capacity."); checks.Add(Op("$lte", "$input.inspectedContainers", "$capacity.remainingContainers"));
            } else {
                fields.AddRange(new BsonDocument { { "draftHash", Hash }, { "sequence", S.Number(1, long.MaxValue - 2) }, { "before", Capacity() }, { "after", Capacity() },
                    { "previousInspectionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "state", S.Choice("submitted_quarantine") },
                    { "reservationId", S.Number(1) }, { "evidence", S.Object(new() { { "id", S.Number(1) }, { "version", S.Number(1) }, { "sha256", Hash } }) }, { "pinId", S.Number(1) } });
                Totals(checks, "$before."); Totals(checks, "$after."); checks.Add(Op("$eq", "$before.receivedContainers", "$after.receivedContainers"));
                checks.Add(Op("$eq", "$sequence", "$after.version")); checks.Add(Op("$eq", "$sequence", Op("$add", "$before.version", 1L)));
                checks.Add(Op("$eq", "$after.inspectedContainers", Op("$add", "$before.inspectedContainers", "$input.inspectedContainers")));
                foreach (var field in new[] { "previousInspectionId", "previousHash" }) checks.Add(Op("$eq", Op("$eq", "$sequence", 1L), Op("$eq", "$" + field, BsonNull.Value)));
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in name == B.Heads ? new[] { "createdAt", "updatedAt" } : new[] { "recordedAt" }) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void Totals(BsonArray checks, string prefix) { checks.Add(Op("$eq", prefix + "receivedContainers", Op("$add", prefix + "inspectedContainers", prefix + "remainingContainers")));
        checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + "inspectedContainers", 0L))); }
    private static BsonDocument Op(string op, params BsonValue[] args) => new(op, new BsonArray(args));
}

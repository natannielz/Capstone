using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementSupplierPayableSchemas
{
    internal static readonly string[] Collections = B.Collections;
    private static BsonDocument Id(long min = 1) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Bool => new("bsonType", "bool");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Code => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" } };
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Money(bool positive = false) => new() { { "bsonType", "string" },
        { "pattern", positive ? @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" : @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Version => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Header() => new() { { "_id", Id() }, { "schemaVersion", Version } };
    private static BsonDocument Person => S.Object(new() { { "userId", Id() }, { "personId", Id() }, { "verification", S.Choice("Verified") }, { "version", Id() } });
    private static BsonDocument People(int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", Person } };
    private static BsonDocument Account => S.Object(new() { { "mappingKey", Code }, { "accountId", Id() }, { "accountCode", Code } });
    private static BsonDocument PolicyFields(bool live)
    {
        var fields = Header(); fields.AddRange(new BsonDocument { { "revision", Id() }, { "active", Bool },
            { "location", ProcurementRequestSchemas.Location() }, { "currency", S.Choice("IDR") },
            { "approvedAt", Date }, { "expiresAt", Date }, { "proposedBy", Id() }, { "approvedBy", Id() },
            { "people", People(2, 2) }, { "sourceTreatment", S.Choice("receipt_accrual_to_payable") },
            { "applicationTreatment", S.Choice("verified_advance_to_payable") }, { "bookId", Id() }, { "chartVersionId", Id() },
            { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) } });
        foreach (var role in new[] { "receiptAccrual", "supplierPayable", "supplierAdvance" }) {
            fields.Add(role + "Mapping", Code); fields.Add(role + "AccountId", Id()); fields.Add(role + "AccountCode", Code);
        }
        if (live) fields.Add("fence", Id(0)); return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields(false));
    internal static BsonDocument Accounting() => S.Object(new() { { "bookId", Id() }, { "chartVersionId", Id() },
        { "periodId", Id() }, { "periodMonth", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])\z" } } },
        { "receiptAccrual", Account }, { "supplierPayable", Account }, { "supplierAdvance", Account } });
    internal static BsonDocument Input() => S.Object(new() { { "kind", S.Choice("recognize", "apply_advance") },
        { "location", ProcurementRequestSchemas.Location() }, { "valuationAllocationId", Nullable(Id()) },
        { "payableId", Nullable(Id()) }, { "settlementId", Nullable(Id()) }, { "amount", Nullable(Money(true)) },
        { "policyId", Id() }, { "policyRevision", Id() }, { "postingDate", Day }, { "reason", S.Text(1000) } });
    private static BsonDocument RecognizeSource() => S.Object(new() { { "kind", S.Choice("recognize") },
        { "valuationAllocationId", Id() }, { "valuationAllocationHash", Hash }, { "valuationDecisionId", Id() },
        { "valuationJournalId", Id() }, { "receiptId", Id() }, { "invoiceDocumentId", Id() }, { "invoiceMatchId", Id() },
        { "matchLineNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 100 } } },
        { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } },
        { "orderId", Id() }, { "commercialReference", S.Text(128) }, { "invoiceDocumentReference", S.Text(128) },
        { "supplierId", Id() }, { "supplierName", S.Text(160) }, { "location", ProcurementRequestSchemas.Location() },
        { "amount", Money(true) }, { "netAmount", Money() }, { "taxAmount", Money() }, { "chargeAmount", Money() },
        { "sourceAccount", Account }, { "sourceBookId", Id() } });
    private static BsonDocument ApplySource() => S.Object(new() { { "kind", S.Choice("apply_advance") },
        { "payableId", Id() }, { "payableHash", Hash }, { "settlementId", Id() }, { "settlementHash", Hash },
        { "disbursementId", Id() }, { "settlementJournalId", Id() }, { "valuationAllocationId", Id() },
        { "orderId", Id() }, { "commercialReference", S.Text(128) }, { "invoiceDocumentReference", S.Text(128) },
        { "supplierId", Id() }, { "supplierName", S.Text(160) }, { "location", ProcurementRequestSchemas.Location() },
        { "amount", Money(true) }, { "sourcePayableAccount", Account }, { "sourceAdvanceAccount", Account },
        { "sourceBookId", Id() }, { "advanceBookId", Id() } });
    internal static BsonDocument Source() => new("anyOf", new BsonArray { RecognizeSource(), ApplySource() });
    private static BsonDocument RecognizeCapacity() => S.Object(new() { { "valuationAllocationId", Id() },
        { "amount", Money(true) }, { "recognized", Bool }, { "payableId", Nullable(Id()) } });
    private static BsonDocument PayableCapacity() => S.Object(new() { { "payableId", Id() }, { "version", Id(0) },
        { "totalAmount", Money(true) }, { "appliedAmount", Money() }, { "remainingAmount", Money() },
        { "lastApplicationId", Nullable(Id()) }, { "lastHash", Nullable(Hash) } });
    private static BsonDocument AdvanceCapacity() => S.Object(new() { { "settlementId", Id() }, { "version", Id(0) },
        { "totalAmount", Money(true) }, { "appliedAmount", Money() }, { "remainingAmount", Money() },
        { "lastApplicationId", Nullable(Id()) }, { "lastHash", Nullable(Hash) } });
    private static BsonDocument ApplyCapacity() => S.Object(new() { { "payable", PayableCapacity() }, { "advance", AdvanceCapacity() } });
    internal static BsonDocument Capacity() => new("anyOf", new BsonArray { RecognizeCapacity(), ApplyCapacity() });
    private static BsonDocument Head(string kind)
    {
        var fields = Header(); fields.Add("sourceHash", Hash).Add("createdAt", Date).Add("updatedAt", Date).Add("fence", Id(0));
        fields.AddRange((kind == B.PayableHeads ? PayableCapacity() : AdvanceCapacity())["properties"].AsBsonDocument);
        return S.Object(fields);
    }
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Payables => [new("valuationAllocationId", 1), new("proposalId", 1), new("decisionId", 1), new("journalId", 1)],
        B.Applications => [new("proposalId", 1), new("decisionId", 1), new("journalId", 1),
            new() { { "payableId", 1 }, { "payableSequence", 1 } }, new() { { "settlementId", 1 }, { "advanceSequence", 1 } }],
        B.PayableHeads or B.AdvanceHeads => [new("_id", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name)
    {
        var fields = Header();
        switch (name) {
            case B.Policies: fields = PolicyFields(true); break;
            case B.PayableHeads: case B.AdvanceHeads: return new("$jsonSchema", Head(name));
            case B.Proposals: case B.Decisions:
                fields.AddRange(new BsonDocument { { "version", Id() }, { "location", ProcurementRequestSchemas.Location() },
                    { "input", Input() }, { "source", Source() }, { "capacity", Capacity() }, { "policy", Policy() },
                    { "accounting", Accounting() }, { "amount", Money(true) }, { "binding", Hash }, { "people", People(1, 4) },
                    { "operation", S.Choice(name == B.Proposals ? B.ProposeOperation : B.DecideOperation) },
                    { "actorId", Id() }, { "actorGrantVersion", Id() }, { "recordedAt", Date }, { "requestKey", S.Text(128) },
                    { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields);
                if (name == B.Decisions) fields.AddRange(new BsonDocument { { "proposalId", Id() }, { "proposalHash", Hash },
                    { "proposalMakerId", Id() },
                    { "decision", S.Choice("approve", "reject") }, { "payableBefore", Nullable(PayableCapacity()) },
                    { "payableAfter", Nullable(PayableCapacity()) }, { "advanceBefore", Nullable(AdvanceCapacity()) },
                    { "advanceAfter", Nullable(AdvanceCapacity()) }, { "payableId", Nullable(Id()) },
                    { "applicationId", Nullable(Id()) }, { "journalId", Nullable(Id()) },
                    { "journalFingerprint", Nullable(Hash) }, { "postingFence", S.Number(0, 1) } });
                break;
            case B.Payables:
                fields.AddRange(new BsonDocument { { "proposalId", Id() }, { "decisionId", Id() }, { "valuationAllocationId", Id() },
                    { "valuationAllocationHash", Hash }, { "amount", Money(true) }, { "location", ProcurementRequestSchemas.Location() },
                    { "orderId", Id() }, { "commercialReference", S.Text(128) }, { "invoiceDocumentReference", S.Text(128) },
                    { "supplierId", Id() }, { "supplierName", S.Text(160) }, { "accounting", Accounting() }, { "journalId", Id() },
                    { "journalFingerprint", Hash }, { "recordedAt", Date }, { "snapshotHash", Hash } }); break;
            case B.Applications:
                fields.AddRange(new BsonDocument { { "proposalId", Id() }, { "decisionId", Id() }, { "proposalHash", Hash },
                    { "payableId", Id() }, { "settlementId", Id() }, { "amount", Money(true) }, { "payableSequence", Id() },
                    { "advanceSequence", Id() }, { "payableBefore", PayableCapacity() }, { "payableAfter", PayableCapacity() },
                    { "advanceBefore", AdvanceCapacity() }, { "advanceAfter", AdvanceCapacity() }, { "journalId", Id() },
                    { "recordedAt", Date }, { "snapshotHash", Hash } }); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return new("$jsonSchema", S.Object(fields));
    }
}

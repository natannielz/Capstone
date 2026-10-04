using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementAdvanceReturnSchemas
{
    internal static readonly string[] Collections = B.Collections;
    internal static readonly string[] ExtendedPayableCollections = [P.Proposals, P.Decisions, P.Applications, P.AdvanceHeads];
    internal static readonly (string Table, string Name, BsonDocument Key)[] ReverseIndexes = [
        (B.Proposals, "ix_advance_return_proposal_settlement", new() { { "source.lines.settlementId", 1 }, { "_id", 1 } }),
        (B.Lines, "ix_advance_return_line_proposal", new("proposalId", 1)),
        ("auditEvents", "ix_advance_return_audit_proposal", new() { { "kind", 1 }, { "result.proposalId", 1 } }),
        ("outboxEvents", "ix_advance_return_outbox_proposal", new() { { "kind", 1 }, { "payload.proposalId", 1 } }),
        ("idempotencyRecords", "ix_advance_return_operation_proposal", new() { { "operation", 1 }, { "result.proposalId", 1 } })];
    private static BsonDocument Id(long minimum = 1) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Code => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" } };
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Money(bool positive = false) => new() { { "bsonType", "string" },
        { "pattern", positive ? @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" : @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Number(int value) => new() { { "bsonType", "int" }, { "enum", new BsonArray { value } } };
    private static BsonDocument Header() => new() { { "_id", Id() }, { "schemaVersion", Number(1) } };
    private static BsonDocument Account => S.Object(new() { { "mappingKey", Code }, { "accountId", Id() }, { "accountCode", Code } });
    private static BsonDocument People(int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum },
        { "items", S.Object(new() { { "userId", Id() }, { "personId", Id() }, { "verification", S.Choice("Verified") }, { "version", Id() } }) } };
    private static BsonDocument Lines(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };

    internal static BsonDocument LegacyAdvanceState() => S.Object(new() { { "settlementId", Id() }, { "version", Id(0) },
        { "totalAmount", Money(true) }, { "appliedAmount", Money() }, { "remainingAmount", Money() },
        { "lastApplicationId", Nullable(Id()) }, { "lastHash", Nullable(Hash) } });
    internal static BsonDocument AdvanceStateV2() => S.Object(new() { { "stateVersion", Number(2) }, { "settlementId", Id() },
        { "version", Id() }, { "totalAmount", Money(true) }, { "appliedAmount", Money() }, { "returnedAmount", Money() },
        { "remainingAmount", Money() }, { "lastEffectKind", S.Choice("apply_advance", "return") },
        { "lastEffectId", Id() }, { "lastHash", Hash } });
    internal static BsonDocument AdvanceState() => new("anyOf", new BsonArray { LegacyAdvanceState(), AdvanceStateV2() });

    // Published v1 validators and immutable snapshots remain exact. Only migration76 installs this extension.
    internal static BsonDocument CurrentPayableValidator(string name)
    {
        var legacy = ProcurementSupplierPayableSchemas.Validator(name);
        if (!ExtendedPayableCollections.Contains(name)) return legacy;
        var schema = legacy["$jsonSchema"].DeepClone().AsBsonDocument;
        var fields = schema["properties"].AsBsonDocument;
        if (name == P.AdvanceHeads) {
            fields.Remove("lastApplicationId");
            foreach (var field in AdvanceStateV2()["properties"].AsBsonDocument) fields[field.Name] = field.Value;
            schema["required"] = new BsonArray(fields.Names);
        } else if (name == P.Applications) {
            fields["advanceBefore"] = AdvanceState(); fields["advanceAfter"] = AdvanceState();
        } else {
            fields["capacity"]["anyOf"][1]["properties"]["advance"] = AdvanceState();
            if (name == P.Decisions) {
                fields["advanceBefore"] = Nullable(AdvanceState()); fields["advanceAfter"] = Nullable(AdvanceState());
            }
        }
        return new("$or", new BsonArray { legacy, new BsonDocument("$jsonSchema", schema) });
    }

    private static BsonDocument PolicyFields(bool live)
    {
        var fields = Header(); fields.AddRange(new BsonDocument { { "revision", Id() }, { "active", new BsonDocument("bsonType", "bool") },
            { "location", ProcurementRequestSchemas.Location() }, { "currency", S.Choice("IDR") }, { "approvedAt", Date }, { "expiresAt", Date },
            { "proposedBy", Id() }, { "approvedBy", Id() }, { "people", People(2, 2) },
            { "treatment", S.Choice("whole_verified_supplier_advance_return") }, { "bookId", Id() }, { "chartVersionId", Id() },
            { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) } });
        foreach (var role in new[] { "suspense", "supplierAdvance" }) {
            fields.Add(role + "Mapping", Code); fields.Add(role + "AccountId", Id()); fields.Add(role + "AccountCode", Code);
        }
        if (live) fields.Add("fence", Id(0)); return fields;
    }
    internal static BsonDocument Input() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() },
        { "receiptId", Id() }, { "policyId", Id() }, { "policyRevision", Id() }, { "postingDate", Day }, { "reason", S.Text(1000) },
        { "lines", Lines(S.Object(new() { { "settlementId", Id() }, { "amount", Money(true) } })) } });
    internal static BsonDocument Accounting() => S.Object(new() { { "bookId", Id() }, { "chartVersionId", Id() }, { "periodId", Id() },
        { "periodMonth", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])\z" } } },
        { "suspense", Account }, { "supplierAdvance", Account } });
    internal static BsonDocument Source() => S.Object(new() { { "receiptId", Id() }, { "receiptHash", Hash }, { "receiptAmount", Money(true) },
        { "bookedDate", Day }, { "bankReference", Nullable(S.Text(160)) }, { "bankAccountId", Id() }, { "bankAccountVersionId", Id() },
        { "location", ProcurementRequestSchemas.Location() }, { "supplierId", Id() }, { "supplierName", S.Text(160) },
        { "orderId", Id() }, { "commercialReference", S.Text(128) }, { "bookId", Id() }, { "suspenseAccount", Account },
        { "lines", Lines(S.Object(new() { { "settlementId", Id() }, { "settlementHash", Hash }, { "disbursementId", Id() },
            { "settlementAmount", Money(true) }, { "returnAmount", Money(true) }, { "sourceAdvanceAccount", Account } })) } });
    internal static BsonDocument Capacity() => S.Object(new() { { "lines", Lines(S.Object(new() {
        { "settlementId", Id() }, { "advance", AdvanceState() } })) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Lines => [new() { { "decisionId", 1 }, { "settlementId", 1 } }, new() { { "settlementId", 1 }, { "advanceSequence", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name)
    {
        var fields = Header();
        if (name == B.Policies) fields = PolicyFields(true);
        else if (name == B.Lines) fields.AddRange(new BsonDocument { { "proposalId", Id() }, { "decisionId", Id() }, { "proposalHash", Hash },
            { "receiptId", Id() }, { "settlementId", Id() }, { "settlementHash", Hash }, { "amount", Money(true) },
            { "advanceSequence", Id() }, { "advanceBefore", AdvanceState() }, { "advanceAfter", AdvanceStateV2() },
            { "journalId", Id() }, { "recordedAt", Date }, { "snapshotHash", Hash } });
        else if (name is B.Proposals or B.Decisions) {
            fields.AddRange(new BsonDocument { { "version", Id() }, { "location", ProcurementRequestSchemas.Location() }, { "input", Input() },
                { "source", Source() }, { "capacity", Capacity() }, { "policy", S.Object(PolicyFields(false)) }, { "accounting", Accounting() },
                { "amount", Money(true) }, { "binding", Hash }, { "people", People(1, 4) },
                { "operation", S.Choice(name == B.Proposals ? B.ProposeOperation : B.DecideOperation) }, { "actorId", Id() },
                { "actorGrantVersion", Id() }, { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } });
            S.Metadata(fields);
            if (name == B.Decisions) fields.AddRange(new BsonDocument { { "proposalId", Id() }, { "proposalHash", Hash }, { "proposalMakerId", Id() },
                { "decision", S.Choice("approve", "reject") }, { "receiptId", Id() }, { "receiptHash", Hash }, { "supplierId", Id() }, { "orderId", Id() },
                { "returnLineIds", new BsonDocument { { "bsonType", "array" }, { "maxItems", 100 }, { "uniqueItems", true }, { "items", Id() } } },
                { "journalId", Nullable(Id()) }, { "journalFingerprint", Nullable(Hash) }, { "postingFence", S.Number(0, 1) } });
        } else throw new ArgumentOutOfRangeException(nameof(name));
        return new("$jsonSchema", S.Object(fields));
    }
}

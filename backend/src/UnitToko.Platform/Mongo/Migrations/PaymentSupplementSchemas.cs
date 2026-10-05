using MongoDB.Bson;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static partial class PaymentSupplementSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Heads, B.Versions, B.Attachments];
    internal static BsonDocument Long(long minimum = 1, long maximum = long.MaxValue - 2) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Int(int min, int max) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Text(int max) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", max }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Nullable(BsonDocument rule) => new("anyOf", new BsonArray { rule, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    internal static BsonDocument Reference() => Object(new() { { "id", Long() }, { "version", Long(1, 1) }, { "sha256", Hash() } });
    internal static BsonDocument Primary() => Object(new() { { "declarationId", Long() }, { "declarationRevision", Long() }, { "submissionId", Long() }, { "primaryEvidence", Reference() } });
    internal static BsonDocument Scope() => Object(new() { { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() } });
    internal static BsonDocument Original() => Object(new() { { "storeId", Hash() }, { "reservationId", Long() }, { "attemptId", Long() }, { "attemptRevision", Long() },
        { "stageToken", Hash() }, { "generation", Hash() }, { "contentRevision", Long(1, 1) }, { "length", Long(1, UnitToko.Modules.Documents.PdfDocumentExtractionContract.MaximumOriginalBytes) }, { "sha256", Hash() } });
    private static BsonDocument Approvals() => new() { { "bsonType", "array" }, { "minItems", 3 }, { "maxItems", 3 },
        { "items", Object(new() { { "owner", Choice("operations", "finance", "data-owner") }, { "reference", Text(256) }, { "approvedAt", Date() } }) } };
    internal static BsonDocument PolicySnapshot() => Object(new() { { "id", Long() }, { "revision", Long() }, { "scope", Scope() },
        { "decisionRef", Text(256) }, { "decisionHash", Hash() }, { "approvals", Approvals() }, { "maximumSupplements", Int(1, 64) }, { "approvedAt", Date() }, { "expiresAt", Date() } });
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", Int(1, 1) } };
        var terms = new BsonArray();
        if (name == B.Policies) {
            fields.Add("revision", Long()).Add("fence", Long(0)).Add("scope", Scope()).Add("active", new BsonDocument("bsonType", "bool"))
                .Add("decisionRef", Text(256)).Add("decisionHash", Hash()).Add("approvals", Approvals()).Add("maximumSupplements", Int(1, 64))
                .Add("approvedAt", Date()).Add("expiresAt", Date()).Add("createdAt", Date()).Add("updatedAt", Date());
            terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$lt", new BsonArray { "$approvedAt", "$expiresAt" }), new BsonDocument("$lte", new BsonArray { "$createdAt", "$updatedAt" }),
                new BsonDocument("$lte", new BsonArray { new BsonDocument("$subtract", new BsonArray { "$expiresAt", "$approvedAt" }), 86_400_000L * 90 }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { "$approvals.owner", new BsonArray() })), 3 }) })));
        } else {
            fields.Add("primary", Primary()).Add("primarySubmissionHash", Hash()).Add("uploaderId", Long()).Add("scope", Scope());
            if (name == B.Heads) {
                fields.Add("revision", Long(1, 2)).Add("state", Choice("draft", "attached")).Add("versionId", Long()).Add("versionHash", Hash())
                    .Add("createdAt", Date()).Add("updatedAt", Date()).Add("fence", Long(0));
                terms.Add(new BsonDocument("$or", new BsonArray { new BsonDocument { { "revision", 1L }, { "state", "draft" } }, new BsonDocument { { "revision", 2L }, { "state", "attached" } } }));
            } else if (name == B.Versions) {
                fields.Add("supplementId", Long()).Add("revision", Long(1, 2)).Add("state", Choice("draft", "attached")).Add("policy", PolicySnapshot())
                    .Add("previousVersionId", Nullable(Long())).Add("previousHash", Nullable(Hash())).Add("attachmentHash", Nullable(Hash()))
                    .Add("reservationId", Nullable(Long())).Add("evidence", Nullable(Reference()));
                Metadata(fields);
                terms.Add(new BsonDocument("$or", new BsonArray {
                    new BsonDocument { { "revision", 1L }, { "state", "draft" }, { "operation", B.CreateOperation }, { "previousVersionId", BsonNull.Value },
                        { "previousHash", BsonNull.Value }, { "attachmentHash", BsonNull.Value }, { "reservationId", BsonNull.Value }, { "evidence", BsonNull.Value } },
                    new BsonDocument { { "revision", 2L }, { "state", "attached" }, { "operation", B.AttachOperation }, { "previousVersionId", new BsonDocument("$type", "long") },
                        { "previousHash", new BsonDocument("$type", "string") }, { "attachmentHash", new BsonDocument("$type", "string") },
                        { "reservationId", new BsonDocument("$type", "long") }, { "evidence", new BsonDocument("$type", "object") } } }));
            } else if (name == B.Attachments) {
                fields.Add("supplementRevision", Long(2, 2)).Add("historyVersionId", Long()).Add("reservationId", Long()).Add("evidence", Reference())
                    .Add("actorGrantVersion", Long()).Add("pinId", Long()).Add("original", Original()).Add("policy", PolicySnapshot()).Add("attachedAt", Date())
                    .Add("requestKey", Text(128)).Add("bodyHash", Hash()).Add("reason", Text(1000)).Add("correlationId", Text(128)).Add("snapshotHash", Hash());
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$original.reservationId", "$reservationId" }),
                    new BsonDocument("$eq", new BsonArray { "$evidence.sha256", "$original.sha256" }),
                    new BsonDocument("$ne", new BsonArray { "$evidence", "$primary.primaryEvidence" }) })));
            } else throw new ArgumentException("Unknown supplement owner", nameof(name));
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", Object(fields))); return new("$and", terms);
    }
    private static void Metadata(BsonDocument fields) => fields.Add("operation", Choice(B.CreateOperation, B.AttachOperation)).Add("auditId", Long())
        .Add("actorId", Long()).Add("actorGrantVersion", Long()).Add("requestKey", Text(128)).Add("bodyHash", Hash()).Add("reason", Text(1000))
        .Add("correlationId", Text(128)).Add("recordedAt", Date()).Add("snapshotHash", Hash());
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        B.Policies => [("ux_paymentSupplement_policy_scope", new() { { "scope.organizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 } }, true)],
        B.Heads => [("ix_paymentSupplement_primary_page", new() { { "primary.submissionId", 1 }, { "_id", -1 } }, false)],
        B.Versions => [("ux_paymentSupplement_revision", new() { { "supplementId", 1 }, { "revision", 1 } }, true),
            ("ux_paymentSupplement_original_key", new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, true)],
        B.Attachments => [("ux_paymentSupplement_reservation", new("reservationId", 1), true), ("ux_paymentSupplement_pin", new("pinId", 1), true),
            ("ux_paymentSupplement_pdf_ref", new("evidence", 1), true)],
        _ => throw new ArgumentException("Unknown supplement owner", nameof(name))
    };
    internal static BsonDocument JobBranch() => new("$and", new BsonArray {
        new BsonDocument("kind", B.JobKind), Translate(EvidenceSchemas.Validator("outboxEvents")),
        new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "payload", "sourceKey" } },
            { "properties", new BsonDocument {
                { "sourceKey", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\Apayment\.supplement-document-ocr\.requested:[1-9][0-9]{0,18}:2\z" } } },
                { "processingFence", Long(0) }, { "attemptLimit", Int(1, 5) }, { "attempts", Int(0, 5) },
                { "payload", Object(new() { { "sourceKind", Choice(B.SourceKind) }, { "supplementId", Long() }, { "supplementRevision", Long(2, 2) },
                    { "primary", Primary() }, { "primarySubmissionHash", Hash() }, { "evidence", Reference() }, { "reservationId", Long() }, { "uploaderId", Long() },
                    { "pinId", Long() }, { "attachmentHash", Hash() }, { "original", Original() } }) },
                { "processingReceipt", Object(new() { { "pdfDocumentExtraction", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
                    { "sourceKind", Choice(B.SourceKind) }, { "resultId", Long() }, { "resultHash", Hash() }, { "readAuditId", Long() }, { "auditId", Long() },
                    { "bindingHash", Hash() }, { "originalHash", Hash() }, { "pipelineId", Hash() } }) } } } }),
        new BsonDocument("$or", new BsonArray { new BsonDocument("status", new BsonDocument("$ne", "completed")), new BsonDocument("processingReceipt", new BsonDocument("$exists", true)) }),
        new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
            new BsonDocument("$eq", new BsonArray { "$payload.reservationId", "$payload.original.reservationId" }),
            new BsonDocument("$eq", new BsonArray { "$payload.evidence.sha256", "$payload.original.sha256" }),
            new BsonDocument("$ne", new BsonArray { "$payload.evidence", "$payload.primary.primaryEvidence" }),
            new BsonDocument("$eq", new BsonArray { "$sourceKey", new BsonDocument("$concat", new BsonArray { B.JobKind + ":", new BsonDocument("$toString", "$payload.supplementId"), ":2" }) }) })) });
    internal static BsonDocument ExtendedOutboxValidator() => new("$or", new BsonArray {
        new BsonDocument("$and", new BsonArray { PdfDocumentExtractionSchemas.ExtendedOutboxValidator(), new BsonDocument("kind", new BsonDocument("$ne", B.JobKind)) }), JobBranch() });
    internal static BsonDocument ExtendOutbox(BsonDocument installed) {
        var before = PdfDocumentExtractionSchemas.ExtendedOutboxValidator(); var after = ExtendedOutboxValidator(); var recognized = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { recognized = true; return value.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(value, before)) { recognized = true; return after.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray) copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(installed); return recognized ? result : throw new InvalidOperationException("Supplement migration requires the actual reviewed M86 outbox owner");
    }
    private static BsonValue Translate(BsonValue value) => value.IsString
        ? value.AsString == "evidence.validation.requested" ? new BsonString(B.JobKind) : value.DeepClone()
        : value.IsBsonArray ? new BsonArray(value.AsBsonArray.Select(Translate))
        : value.IsBsonDocument ? new BsonDocument(value.AsBsonDocument.Elements.Select(element => new BsonElement(element.Name, Translate(element.Value)))) : value.DeepClone();
}

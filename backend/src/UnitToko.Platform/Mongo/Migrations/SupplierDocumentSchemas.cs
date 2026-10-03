using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierDocumentSchemas
{
    internal static readonly string[] Collections = [B.Drafts, B.Submissions, B.Matches, B.Identities, B.Heads, B.InspectionHeads, B.OrderHeads];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Money => new() { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Int(int min = 1, int max = 1000000) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Total(long min = 0) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument value, int min = 1, int max = 100) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", value } };
    internal static BsonDocument Input() => S.Object(new() {
        { "location", ProcurementRequestSchemas.Location() }, { "anchorOrderId", S.Number(1) }, { "anchorOrderVersion", S.Number(1, 1) }, { "anchorOrderApprovalId", S.Number(1) },
        { "kind", S.Choice("invoice", "sph") }, { "documentReference", S.Text(128) }, { "documentDate", Nullable(Day) }, { "currency", S.Choice("IDR") },
        { "documentTotal", Nullable(Money) }, { "discrepancy", Nullable(S.Text(1000)) }, { "lines", Array(S.Object(new() {
            { "sourceLineReference", S.Text(128) }, { "description", S.Text(512) }, { "sourceSystem", Nullable(S.Text(64)) }, { "sourceAccount", Nullable(S.Text(128)) },
            { "sourceCode", Nullable(S.Text(128)) }, { "packages", Nullable(Int()) }, { "netPricePerPackage", Nullable(Money) }, { "taxAmount", Nullable(Money) },
            { "chargeAmount", Nullable(Money) }, { "relatedInvoiceReference", Nullable(S.Text(128)) }, { "statementAmount", Nullable(Money) } })) } });
    private static BsonDocument MatchLineFields() => new() {
        { "documentLineNumber", Int(1, 100) }, { "packages", Nullable(Int()) }, { "inspectionId", Nullable(S.Number(1)) }, { "inspectionLineNumber", Nullable(Int(1, 100)) },
        { "invoiceMatchId", Nullable(S.Number(1)) }, { "invoiceMatchLineNumber", Nullable(Int(1, 100)) }, { "netAmount", Nullable(Money) }, { "taxAmount", Nullable(Money) },
        { "chargeAmount", Nullable(Money) }, { "statementAmount", Nullable(Money) }, { "discrepancy", Nullable(S.Text(1000)) } };
    internal static BsonDocument MatchInput() => S.Object(new() { { "documentId", S.Number(1) }, { "documentRevision", S.Number(1, 1) }, { "reviewedInput", Input() },
        { "ocrResultId", Nullable(S.Number(1)) }, { "lines", Array(S.Object(MatchLineFields())) } });
    private static BsonDocument CapacityLines() => Array(S.Object(new() {
        { "lineNumber", Int(1, 100) }, { "packages", Nullable(Int(0)) }, { "matchedPackages", S.Number(0, 1000000) }, { "remainingPackages", Nullable(S.Number(0, 1000000)) },
        { "matchedNetAmount", Money }, { "matchedTaxAmount", Money }, { "matchedChargeAmount", Money }, { "matchedStatementAmount", Money } }));
    internal static BsonDocument Capacity() => S.Object(new() { { "version", Total() }, { "lines", CapacityLines() } });
    private static BsonDocument OrderView() {
        var input = PurchaseOrderSchemas.Input()["properties"].AsBsonDocument;
        return S.Object(new() { { "id", S.Number(1) }, { "version", S.Number(1, 1) }, { "approvalId", S.Number(1) }, { "proposalId", S.Number(1) },
            { "route", S.Choice("omi_regular", "ddo", "smart", "pasar_kering") }, { "businessDate", Day }, { "currency", S.Choice("IDR") },
            { "supplier", S.Object(new() { { "id", S.Number(1) }, { "versionId", S.Number(1) }, { "version", S.Number(1) }, { "subjectReference", S.Text(128) }, { "displayName", S.Text(160) } }) },
            { "total", Money }, { "requiredAdvance", Money }, { "paymentTerms", input["paymentTerms"].DeepClone() }, { "commercialReference", input["commercialReference"].DeepClone() },
            { "externalOrderReference", Nullable(S.Text(128)) }, { "lines", Array(S.Object(new() {
                { "skuId", S.Number(1) }, { "skuRevision", S.Number(1) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) }, { "skuCode", S.Text(64) },
                { "familyName", S.Text(160) }, { "packageLabel", S.Text(160) }, { "packageKind", S.Choice("loose", "sealed", "other") }, { "baseUnitCode", S.Text(64) }, { "baseUnitsPerPackage", Int(1, int.MaxValue) },
                { "packages", Int() }, { "specification", Nullable(S.Text(1000)) }, { "sourceSystem", S.Choice("omi", "s_mart", "supplier") }, { "sourceAccountReference", S.Text(128) },
                { "externalItemCode", S.Text(128) }, { "netPricePerPackage", Money }, { "taxAmount", Money }, { "supplierChargeAmount", Money }, { "netAmount", Money }, { "totalAmount", Money },
                { "baseUnits", S.Number(1) } })) } });
    }
    private static BsonDocument ResolvedLine() {
        var fields = MatchLineFields(); fields.AddRange(new BsonDocument { { "state", S.Choice("matched", "discrepancy_pending") },
            { "order", Nullable(OrderView()) }, { "inspectionLine", Nullable(ProcurementInspectionSchemas.Line()) }, { "invoiceDocumentReference", Nullable(S.Text(128)) },
            { "invoiceMatchedAmount", Nullable(Money) } }); return S.Object(fields);
    }
    private static BsonDocument Link() => new("anyOf", new BsonArray {
        new BsonDocument("bsonType", "null"),
        S.Object(new() { { "kind", S.Choice("inspection") }, { "id", S.Number(1) }, { "lineNumber", Int(1, 100) }, { "hash", Hash }, { "noticeId", S.Number(1) },
            { "intakeId", S.Number(1) }, { "orderId", S.Number(1) }, { "orderHash", Hash }, { "requestSkuId", S.Number(1) } }),
        S.Object(new() { { "kind", S.Choice("invoice_match") }, { "id", S.Number(1) }, { "lineNumber", Int(1, 100) }, { "hash", Hash }, { "documentId", S.Number(1) }, { "documentHash", Hash } }) });
    private static BsonDocument Claim() => S.Object(new() { { "kind", S.Choice("inspection", "order") }, { "sourceId", S.Number(1) }, { "skuId", Nullable(S.Number(1)) },
        { "sourceHash", Hash }, { "headId", S.Number(1) }, { "before", Capacity() }, { "after", Capacity() }, { "previousMatchId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) } });
    private static BsonDocument Review() => S.Object(new() { { "capacity", Capacity() }, { "identityVersion", Total(1) },
        { "inspectionCapacities", Array(S.Object(new() { { "inspectionId", S.Number(1) }, { "capacity", Capacity() } }), 0) },
        { "orderCapacities", Array(S.Object(new() { { "orderId", S.Number(1) }, { "requestSkuId", S.Number(1) }, { "capacity", Capacity() } }), 0) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Drafts => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Submissions => [new("draftId", 1), new("pinId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Matches => [new() { { "documentId", 1 }, { "sequence", 1 } }, new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Identities => [new("identityKey", 1), new() { { "organizationId", 1 }, { "shopId", 1 }, { "channelId", 1 }, { "supplierId", 1 }, { "kind", 1 }, { "reference", 1 } }],
        B.OrderHeads => [new() { { "orderId", 1 }, { "requestSkuId", 1 } }], B.Heads or B.InspectionHeads => [new("_id", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Identities) {
            foreach (var key in new[] { "organizationId", "shopId", "channelId", "supplierId", "firstDraftId" }) fields.Add(key, S.Number(1));
            fields.AddRange(new BsonDocument { { "identityKey", Hash }, { "kind", S.Choice("invoice", "sph") }, { "reference", S.Text(128) }, { "version", Total(1) }, { "draftCount", Total(1) } });
        } else fields.Add("location", ProcurementRequestSchemas.Location());
        if (name is B.Identities or B.Heads or B.InspectionHeads or B.OrderHeads) {
            fields.AddRange(new BsonDocument { { "fence", Total() }, { "createdAt", Date }, { "updatedAt", Date } }); dates.AddRange(["createdAt", "updatedAt"]); checks.Add(Op("$gte", "$updatedAt", "$createdAt"));
            if (name != B.Identities) {
                fields.AddRange(new BsonDocument { { "version", Total(name == B.Heads ? 0 : 1) }, { "lines", CapacityLines() }, { "lastMatchId", Nullable(S.Number(1)) }, { "lastHash", Nullable(Hash) } });
                if (name == B.Heads) { fields.Add("draftId", S.Number(1)).Add("documentId", Nullable(S.Number(1))).Add("draftHash", Hash).Add("reviewedInput", Nullable(Input())); checks.Add(Op("$eq", "$_id", "$draftId")); }
                else {
                    fields.Add("sourceHash", Hash);
                    if (name == B.InspectionHeads) { fields.Add("inspectionId", S.Number(1)); checks.Add(Op("$eq", "$_id", "$inspectionId")); }
                    else fields.Add("orderId", S.Number(1)).Add("requestSkuId", S.Number(1));
                }
                foreach (var field in new[] { "lastMatchId", "lastHash" }) checks.Add(Op("$eq", Op("$eq", "$version", 0L), Op("$eq", "$" + field, BsonNull.Value)));
            }
        } else {
            fields.AddRange(new BsonDocument { { "draftId", S.Number(1) }, { "version", S.Number(1, 1) }, { "supplierId", S.Number(1) }, { "identityId", S.Number(1) },
                { "operation", S.Choice(name == B.Drafts ? B.DraftOperation : name == B.Submissions ? B.SubmitOperation : B.MatchOperation) },
                { "binding", Hash }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "people", Array(S.Object(new() { { "userId", S.Number(1) },
                    { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } }), 1, 1) },
                { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields); dates.Add("recordedAt");
            checks.Add(Op("$eq", "$actorId", new BsonDocument("$arrayElemAt", new BsonArray { "$people.userId", 0 })));
            if (name is B.Drafts or B.Submissions) {
                fields.Add("input", Input()).Add("source", ProcurementFundingSchemas.OrderSource());
                checks.Add(Op("$eq", "$location", "$input.location")); checks.Add(Op("$eq", "$input.anchorOrderId", "$source.order._id"));
                checks.Add(Op("$eq", "$input.anchorOrderApprovalId", "$source.order.approvalId")); checks.Add(Op("$eq", "$input.anchorOrderVersion", "$source.order.version"));
                checks.Add(Op("$eq", "$location", "$source.order.location")); checks.Add(Op("$lte", "$source.order.recordedAt", "$recordedAt"));
                if (name == B.Drafts) { fields.Add("identityVersion", Total(1)).Add("submittedDocumentId", Nullable(S.Number(1))).Add("fence", Total()); checks.Add(Op("$eq", "$_id", "$draftId")); }
                else fields.Add("draftHash", Hash).Add("reservationId", S.Number(1)).Add("evidence", PaymentSubmissionSchemas.Reference()).Add("pinId", S.Number(1));
            } else {
                fields.AddRange(new BsonDocument { { "documentId", S.Number(1) }, { "documentHash", Hash }, { "input", MatchInput() }, { "before", Capacity() }, { "after", Capacity() },
                    { "lines", Array(ResolvedLine()) }, { "links", Array(Link()) }, { "claims", Array(Claim(), 0, 200) }, { "review", Review() },
                    { "sequence", Total(1) }, { "previousMatchId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) } });
                checks.Add(Op("$eq", "$documentId", "$input.documentId")); checks.Add(Op("$eq", "$location", "$input.reviewedInput.location")); checks.Add(Op("$eq", "$review.capacity", "$before"));
                checks.Add(Op("$eq", "$sequence", "$after.version")); checks.Add(Op("$eq", "$sequence", Op("$add", "$before.version", 1L)));
                checks.Add(Op("$eq", new BsonDocument("$size", "$lines"), new BsonDocument("$size", "$input.lines"))); checks.Add(Op("$eq", new BsonDocument("$size", "$links"), new BsonDocument("$size", "$lines")));
                foreach (var field in new[] { "previousMatchId", "previousHash" }) checks.Add(Op("$eq", Op("$eq", "$sequence", 1L), Op("$eq", "$" + field, BsonNull.Value)));
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static BsonDocument Op(string name, params BsonValue[] args) => new(name, new BsonArray(args));
}

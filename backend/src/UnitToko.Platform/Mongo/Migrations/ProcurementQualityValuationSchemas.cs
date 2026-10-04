using MongoDB.Bson;
using D = UnitToko.Platform.Procurement.SupplierDocumentBson;
using V = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// The published M70/M73 factories and every v1 canonical row remain unchanged.
internal static class ProcurementQualityValuationSchemas
{
    internal static readonly string[] Collections = [D.Matches, V.Proposals, V.Decisions];
    internal static readonly (string Table, string Name, BsonDocument Key)[] LookupIndexes = [
        (D.Matches, "ix_supplierDocumentMatches_links_quality_allocationId", new() { { "links.quality.allocationId", 1 }, { "_id", 1 } }),
        (V.Proposals, "ix_procurementStockValuationProposals_quality_allocationId", new() { { "quality.allocationId", 1 }, { "_id", 1 } })];
    private static BsonDocument Id => S.Number(1, long.MaxValue - 2);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    internal static BsonDocument QualitySnapshot() => S.Object(new() {
        { "allocationId", Id }, { "allocationHash", PaymentSubmissionSchemas.Hash() }, { "proposalId", Id }, { "proposalHash", PaymentSubmissionSchemas.Hash() },
        { "decisionId", Id }, { "decisionHash", PaymentSubmissionSchemas.Hash() }, { "receiptId", Id }, { "receiptHash", PaymentSubmissionSchemas.Hash() },
        { "receiptReference", S.Text(128) }, { "acceptedPackages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } },
        { "lotReference", S.Text(128) }, { "expiresOn", Nullable(Day) }, { "acceptedCondition", S.Choice("sealed_undamaged", "intact_as_defined") },
        { "putawayReference", S.Text(256) }, { "segregationReference", S.Text(256) }, { "recordedAt", new BsonDocument("bsonType", "date") } });
    internal static BsonDocument QualityResolvedLine() {
        var fields = SupplierDocumentSchemas.ResolvedLine()["properties"].DeepClone().AsBsonDocument;
        fields.Add("qualityAllocationId", Id).Add("quality", QualitySnapshot());
        fields["state"] = S.Choice("matched");
        foreach (var field in new[] { "packages", "inspectionId", "inspectionLineNumber", "netAmount", "taxAmount", "chargeAmount", "order", "inspectionLine" })
            fields[field] = fields[field]["anyOf"][0].DeepClone();
        foreach (var field in new[] { "invoiceMatchId", "invoiceMatchLineNumber", "statementAmount", "discrepancy", "invoiceDocumentReference", "invoiceMatchedAmount" })
            fields[field] = new BsonDocument("bsonType", "null");
        return S.Object(fields);
    }
    internal static BsonDocument CurrentResolvedLine() => new("anyOf", new BsonArray { SupplierDocumentSchemas.ResolvedLine(), QualityResolvedLine() });
    internal static BsonDocument CurrentDocumentValidator(string name) {
        var legacy = SupplierDocumentSchemas.Validator(name);
        if (name != D.Matches) return legacy;
        var v2 = legacy.DeepClone().AsBsonDocument;
        var fields = v2["$and"][0]["$jsonSchema"]["properties"].AsBsonDocument;
        fields["schemaVersion"] = new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } };
        var oldInputLine = fields["input"]["properties"]["lines"]["items"].DeepClone().AsBsonDocument;
        var qualityInputFields = oldInputLine["properties"].DeepClone().AsBsonDocument;
        qualityInputFields.Add("qualityAllocationId", Id);
        foreach (var field in new[] { "packages", "inspectionId", "inspectionLineNumber", "netAmount", "taxAmount", "chargeAmount" })
            qualityInputFields[field] = qualityInputFields[field]["anyOf"][0].DeepClone();
        foreach (var field in new[] { "invoiceMatchId", "invoiceMatchLineNumber", "statementAmount", "discrepancy" })
            qualityInputFields[field] = new BsonDocument("bsonType", "null");
        fields["input"]["properties"]["lines"]["items"] = new BsonDocument("anyOf", new BsonArray { oldInputLine, S.Object(qualityInputFields) });
        fields["input"]["properties"]["reviewedInput"]["properties"]["kind"] = S.Choice("invoice");
        fields["lines"]["items"] = CurrentResolvedLine();
        var links = fields["links"]["items"]["anyOf"].AsBsonArray;
        var qualityLinkFields = links[1]["properties"].DeepClone().AsBsonDocument;
        qualityLinkFields.Add("quality", QualitySnapshot()); links.Add(S.Object(qualityLinkFields));
        // A v2 discriminator is never an excuse to rewrite a clean v1 match.
        v2["$and"].AsBsonArray.Add(new BsonDocument("input.lines.qualityAllocationId", new BsonDocument("$exists", true)));
        var lineVariables = new BsonDocument {
            { "inputLine", new BsonDocument("$arrayElemAt", new BsonArray { "$input.lines", "$$index" }) },
            { "line", new BsonDocument("$arrayElemAt", new BsonArray { "$lines", "$$index" }) },
            { "link", new BsonDocument("$arrayElemAt", new BsonArray { "$links", "$$index" }) } };
        var lineProof = new BsonDocument("$let", new BsonDocument {
            { "vars", lineVariables },
            { "in", new BsonDocument("$cond", new BsonArray {
                Op("$eq", new BsonDocument("$type", "$$inputLine.qualityAllocationId"), "missing"),
                Op("$and", Op("$eq", new BsonDocument("$type", "$$line.quality"), "missing"), Op("$eq", new BsonDocument("$type", "$$line.qualityAllocationId"), "missing"), Op("$eq", new BsonDocument("$type", "$$link.quality"), "missing")),
                Op("$and", Op("$eq", "$$inputLine.qualityAllocationId", "$$line.qualityAllocationId"), Op("$eq", "$$inputLine.qualityAllocationId", "$$line.quality.allocationId"), Op("$eq", "$$line.quality", "$$link.quality")) }) } });
        var lineChecks = new BsonDocument("$map", new BsonDocument {
            { "input", new BsonDocument("$range", new BsonArray { 0, new BsonDocument("$size", "$input.lines") }) }, { "as", "index" }, { "in", lineProof } });
        v2["$and"].AsBsonArray.Add(new BsonDocument("$expr", new BsonDocument("$allElementsTrue", new BsonArray { lineChecks })));
        return new("$or", new BsonArray { legacy, v2 });
    }
    internal static BsonDocument CurrentValuationValidator(string name) {
        var legacy = ProcurementStockValuationSchemas.Validator(name);
        if (name is not (V.Proposals or V.Decisions)) return legacy;
        var v2 = legacy.DeepClone().AsBsonDocument;
        var schema = v2["$and"][0]["$jsonSchema"].AsBsonDocument; var fields = schema["properties"].AsBsonDocument;
        fields["schemaVersion"] = new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } };
        fields.Add("quality", QualitySnapshot()).Add("qualityCapacity", ProcurementQualityAcceptanceSchemas.Capacity());
        schema["required"] = new BsonArray(fields.Names);
        if (name == V.Proposals) fields["matchLine"] = QualityResolvedLine();
        var checks = new BsonArray { Op("$eq", "$quality.receiptId", "$input.receiptId"), Op("$eq", "$quality.receiptHash", "$receiptHash"),
            Op("$lte", "$input.packages", "$quality.acceptedPackages"), Op("$lte", "$quality.recordedAt", "$recordedAt") };
        if (name == V.Proposals) checks.Add(Op("$eq", "$quality", "$matchLine.quality"));
        v2["$and"].AsBsonArray.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        v2["$and"].AsBsonArray.Add(S.DateRange("quality.recordedAt"));
        return new("$or", new BsonArray { legacy, v2 });
    }
    private static BsonDocument Op(string name, params BsonValue[] values) => new(name, new BsonArray(values));
}

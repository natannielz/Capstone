using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using B = UnitToko.Platform.Billing.ReturnEntitlementBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReturnEntitlementSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Requests, B.Pools];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Version(long minimum = 0) => S.Number(minimum, long.MaxValue - 1);
    private static BsonDocument Money(decimal minimum = 0) => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(minimum) },
        { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int minimum = 1, int maximum = 100) => new() {
        { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) },
        { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument LineFields(long minimum) => new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) },
        { "attributedBaseUnits", S.Number(minimum) }, { "net", Money() }, { "tax", Money() }, { "charges", Money() } };
    internal static BsonDocument PolicyFields() {
        var fields = Header(); fields.Add("revision", Version(1)); fields.Add("scope", ReceivingAccountSchemas.Scope());
        fields.Add("netRule", S.Choice("tail_backout")); fields.Add("componentRule", S.Choice("manual_component_allocation")); fields.Add("requiredIdentity", S.Choice("matched"));
        foreach (var field in new[] { "attributionProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "creditNoteProcedureReference", "operatingOwnerReference", "approvalReference" })
            fields.Add(field, S.Text(256));
        fields.Add("active", new BsonDocument("bsonType", "bool")); fields.Add("approvedAt", Date); fields.Add("expiresAt", Date); return fields;
    }
    internal static BsonDocument Input() => S.Object(new() { { "lines", Array(S.Object(new() {
        { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "tax", Money() }, { "charges", Money() } })) } });
    internal static BsonDocument PoolState() => S.Object(new() { { "version", Version() }, { "lastRequestId", Nullable(S.Number(1)) },
        { "lastRequestHash", Nullable(Hash) }, { "lines", Array(S.Object(LineFields(0))) } });
    internal static BsonDocument IssuedLines() => Array(S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) },
        { "acceptedBaseUnits", S.Number(0) }, { "previousBaseUnits", S.Number(0) },
        { "previousNet", Money() }, { "net", Money() }, { "tax", Money() }, { "charges", Money() }, { "packagePrice", Money() },
        { "baseUnitsPerPackage", S.Number(1) }, { "rounding", S.Choice("half_even", "half_up") } }));
    internal static BsonDocument OriginalAccounts() => Array(S.Object(new() { { "component", S.Choice("total", "net", "tax", "charges") },
        { "mappingKey", S.Text(80) }, { "accountId", S.Number(1) }, { "accountCode", S.Text(80) } }), 1, 4);
    internal static BsonDocument Source() {
        var fields = new BsonDocument();
        foreach (var field in new[] { "returnId", "caseId", "invoiceId", "requestedBy", "receivedBy", "inspectedBy", "proposedBy" }) fields.Add(field, S.Number(1));
        foreach (var field in new[] { "caseHash", "custodyHash", "inspectionHash", "proposalHash" }) fields.Add(field, Hash);
        fields.Add("participants", Array(S.Number(1), 2, 6)); fields.Add("action", S.Choice("credit", "refund")); fields.Add("invoice", PaymentAllocationSchemas.InvoiceSource());
        fields.Add("issuedLines", IssuedLines()); fields.Add("originalAccounts", OriginalAccounts());
        fields.Add("inspectionLines", ReturnResolutionSchemas.Validator()["$and"][0]["$jsonSchema"]["properties"]["source"]["properties"]["lines"].DeepClone());
        return S.Object(fields);
    }
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => PaymentAllocationSchemas.Keys(UnitToko.Platform.Billing.PaymentAllocationBson.Policies),
        B.Requests => [new() { { "actorId", 1 }, { "requestKey", 1 } }, new() { { "invoiceId", 1 }, { "version", 1 } }],
        B.Pools => [new("_id", 1)], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static BsonDocument UniqueLines(string field) {
        var identities = new BsonDocument("$map", new BsonDocument { { "input", "$" + field }, { "as", "line" },
            { "in", new BsonArray { "$$line.receiptId", "$$line.skuId" } } });
        return new("$eq", new BsonArray { new BsonDocument("$size", "$" + field), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { identities, new BsonArray() })) });
    }
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var terms = new BsonArray(); var expressions = new BsonArray();
        if (name == B.Policies) {
            fields = PolicyFields(); fields.Add("fence", Version()); terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt"));
            expressions.Add(new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" }));
        } else if (name == B.Pools) {
            fields.AddRange(PoolState()["properties"].DeepClone().AsBsonDocument); fields["version"] = Version(1);
            fields["lastRequestId"] = S.Number(1); fields["lastRequestHash"] = Hash;
            fields.Add("sourceHash", Hash); fields.Add("fence", Version()); fields.Add("snapshotHash", Hash); expressions.Add(UniqueLines("lines"));
        } else if (name == B.Requests) {
            fields.AddRange(new BsonDocument { { "invoiceId", S.Number(1) }, { "caseId", S.Number(1) }, { "action", S.Choice("credit", "refund") },
                { "source", Source() }, { "policy", S.Object(PolicyFields()) }, { "before", PoolState() }, { "version", Version(1) }, { "binding", Hash },
                { "input", Input() }, { "lines", Array(S.Object(LineFields(1))) }, { "net", Money() }, { "tax", Money() }, { "charges", Money() }, { "credit", Money(.01m) },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "requestedAt", Date },
                { "people", Array(ReceivingAccountSchemas.Person(), 2, 7) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields);
            foreach (var field in new[] { "requestedAt", "policy.approvedAt", "policy.expiresAt" }) terms.Add(S.DateRange(field));
            foreach (var (left, right) in new[] { ("$_id", "$source.returnId"), ("$invoiceId", "$source.invoiceId"), ("$invoiceId", "$source.invoice.invoiceId"),
                ("$caseId", "$source.caseId"), ("$action", "$source.action") }) expressions.Add(new BsonDocument("$eq", new BsonArray { left, right }));
            expressions.Add(new BsonDocument("$eq", new BsonArray { "$version", new BsonDocument("$add", new BsonArray { "$before.version", 1L }) }));
            expressions.Add(new BsonDocument("$gte", new BsonArray { "$requestedAt", "$policy.approvedAt" }));
            expressions.Add(new BsonDocument("$lt", new BsonArray { "$requestedAt", "$policy.expiresAt" }));
            foreach (var field in new[] { "net", "tax", "charges" }) expressions.Add(new BsonDocument("$eq", new BsonArray { "$" + field, new BsonDocument("$sum", "$lines." + field) }));
            expressions.Add(new BsonDocument("$eq", new BsonArray { "$credit", new BsonDocument("$add", new BsonArray { "$net", "$tax", "$charges" }) }));
            expressions.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$before.version", 0L }),
                new BsonDocument("$and", new BsonArray { new BsonDocument("$eq", new BsonArray { "$before.lastRequestId", BsonNull.Value }), new BsonDocument("$eq", new BsonArray { "$before.lastRequestHash", BsonNull.Value }) }) }));
            expressions.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$ne", new BsonArray { "$before.lastRequestId", BsonNull.Value }), new BsonDocument("$ne", new BsonArray { "$before.lastRequestHash", BsonNull.Value }) }));
            foreach (var field in new[] { "input.lines", "lines", "before.lines", "source.issuedLines", "source.inspectionLines" }) expressions.Add(UniqueLines(field));
            expressions.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", "$source.participants"), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { "$source.participants", new BsonArray() })) }));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        if (expressions.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", expressions)));
        return new("$and", terms);
    }
}

using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// Proposed version-24 contract; no earlier deployment or migration is changed.
internal static class ShipmentDispatchSchemas
{
    internal static BsonDocument Key() => new() { { "orderId", 1 }, { "orderSequence", 1 } };
    internal static BsonDocument NumberKey() => new() { { "numberEntity", 1 }, { "numberChannel", 1 }, { "numberYear", 1 }, { "number", 1 } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Reference() => S.Object(new() { { "id", S.Number(1) }, { "revision", S.Number(1) } });
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument PreparationField(string field) => ShipmentPreparationSchemas.Validator(ShipmentPreparationBson.Versions)["$and"][0]["$jsonSchema"]["properties"][field].DeepClone().AsBsonDocument;
    internal static BsonDocument Validator() => new("$or", new BsonArray { LegacyValidator(), ValueValidator() });
    internal static BsonDocument LegacyValidator() {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice("fulfillment.shipment") }, { "revision", new BsonDocument { { "bsonType", "long" }, { "enum", new BsonArray { 1L } } } },
            { "orderId", S.Number(1) }, { "suborderId", S.Number(1) }, { "orderSequence", S.Number(1) }, { "decisionRevision", S.Number(1, 103) },
            { "numberEntity", S.Number(1) }, { "numberChannel", S.Number(1) }, { "numberYear", S.Number(1, 9999) }, { "number", S.Number(1) },
            { "preparationId", S.Number(1) }, { "preparationVersionId", S.Number(1) }, { "preparationRevision", S.Number(1) },
            { "courierId", S.Number(1) }, { "courierName", S.Text(256) }, { "courierGrantVersion", S.Number(0) }, { "courierProfileVersion", S.Number(1) },
            { "source", PreparationField("source") }, { "scope", PreparationField("scope") }, { "lines", PreparationField("lines") },
            { "pickedLots", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", DomainValues.MaximumLineQuantity } } } })) },
            { "stockEffects", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "allocation", OrderSubmissionSchemas.Allocation() },
                { "before", S.Object(new() { { "revision", S.Number(1) }, { "remaining", S.Number(1, 1000000) }, { "released", S.Number(0, 1000000) }, { "dispatched", S.Number(0, 1000000) } }) } })) },
            { "admission", S.Object(new() {
                { "seller", S.Object(new() { { "version", Reference() }, { "name", S.Text(200) }, { "address", S.Text(1000) } }) }, { "operatingPolicy", Reference() },
                { "validUntil", new BsonDocument("bsonType", "date") },
                { "lots", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "sourceAuthority", Reference() }, { "costBasis", Reference() },
                    { "packageCost", new BsonDocument { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } } } })) }
            }) },
            { "review", Hash() }, { "bodyHash", Hash() }, { "dispatchedBy", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "requestKey", S.Text(128) }, { "recordedAt", new BsonDocument("bsonType", "date") } };
        S.Metadata(fields);
        var consistent = new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$orderId", "$source.orderId" }), new BsonDocument("$eq", new BsonArray { "$suborderId", "$source.suborderId" }),
                new BsonDocument("$eq", new BsonArray { "$numberEntity", "$scope.fulfillmentOrganizationId" }),
                new BsonDocument("$eq", new BsonArray { "$numberChannel", "$scope.channelId" }),
                new BsonDocument("$eq", new BsonArray { "$numberYear", new BsonDocument("$year", new BsonDocument { { "date", "$recordedAt" }, { "timezone", "Asia/Jakarta" } }) }),
                new BsonDocument("$eq", new BsonArray { "$decisionRevision", "$source.revision" }), new BsonDocument("$gte", new BsonArray { "$recordedAt", "$source.orderedAt" }),
                new BsonDocument("$eq", new BsonArray { "$assignedUserIds", new BsonArray { "$courierId" } }) };
        foreach (var field in PreparationField("scope")["properties"].AsBsonDocument) {
            fields.Add(field.Name, field.Value.DeepClone());
            consistent.Add(new BsonDocument("$eq", new BsonArray { "$" + field.Name, "$scope." + field.Name }));
        }
        fields.Add("assignedUserIds", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Number(1) } });
        consistent.Add(new BsonDocument("$lt", new BsonArray { "$recordedAt", "$admission.validUntil" }));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"), S.DateRange("admission.validUntil"),
            new BsonDocument("$expr", new BsonDocument("$and", consistent)) });
    }
    private static BsonDocument ValueValidator() {
        BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
        var money = new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
        BsonDocument State() => S.Object(new() { { "remainingPackages", S.Number(0) }, { "remainingValue", money.DeepClone() }, { "revision", S.Number(1, long.MaxValue - 2) } });
        BsonDocument Core(string name) => AccountingSchemas.Validator(name)["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
        var account = S.Object(new() { { "mappingKey", S.Text(64) }, { "accountId", S.Number(1) }, { "accountCode", S.Text(64) } });
        var key = Core("accountingBooks")["properties"]["key"].DeepClone().AsBsonDocument;
        var book = Core("accountingBooks"); book["properties"].AsBsonDocument.Remove("fence"); book["required"].AsBsonArray.Remove("fence");
        var accounting = S.Object(new() { { "key", key }, { "postingDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } } },
            { "setupProposalId", S.Number(1) }, { "setupProposalHash", Hash() }, { "setupDecisionId", S.Number(1) }, { "setupDecisionHash", Hash() },
            { "setupProposedAt", new BsonDocument("bsonType", "date") }, { "setupApprovedAt", new BsonDocument("bsonType", "date") },
            { "book", book }, { "chart", Core("accountingChartVersions") }, { "periodId", S.Number(1) }, { "periodMonth", S.Text(7) },
            { "inventory", account.DeepClone() }, { "cogs", account.DeepClone() } });
        var physical = InventorySchemas.BalanceValidator()["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
        var finite = S.Object(new() { { "total", money.DeepClone() }, { "accounting", accounting }, { "lots", Array(S.Object(new() {
            { "reservationId", S.Number(1) }, { "balanceId", S.Number(1) }, { "policy", S.Choice(UnitToko.Modules.Inventory.LotValuePolicy.ProportionalFloorPolicy) },
            { "physicalBefore", physical.DeepClone() }, { "before", State() }, { "after", State() }, { "transferredValue", money.DeepClone() } })) } });
        var effects = Array(S.Object(new() { { "reservationId", S.Number(1) }, { "balanceId", S.Number(1) }, { "packages", S.Number(1) },
            { "before", physical.DeepClone() }, { "after", physical.DeepClone() }, { "valueBefore", State() }, { "valueAfter", State() },
            { "transferredValue", money.DeepClone() }, { "movementId", Nullable(S.Number(1)) }, { "transitionId", Nullable(S.Number(1)) }, { "transitionHash", Nullable(Hash()) } }));
        var value = LegacyValidator().DeepClone().AsBsonDocument; var fields = value["$and"][0]["$jsonSchema"]["properties"].AsBsonDocument;
        fields["schemaVersion"]["enum"] = new BsonArray { 2 };
        var admission = fields["admission"].AsBsonDocument; admission["properties"].AsBsonDocument.Add("finiteValue", finite); admission["required"].AsBsonArray.Add("finiteValue");
        foreach (var field in new BsonDocument { { "phase", S.Choice("preparing", "committed") }, { "causeHash", Hash() }, { "postingFence", S.Number(0, 1) },
            { "valueEffects", effects }, { "journalId", Nullable(S.Number(1)) }, { "journalFingerprint", Nullable(Hash()) } }) {
            fields.Add(field); value["$and"][0]["$jsonSchema"]["required"].AsBsonArray.Add(field.Name);
        }
        BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
        BsonDocument Ne(BsonValue left, BsonValue right) => new("$ne", new BsonArray { left, right });
        BsonDocument And(params BsonValue[] terms) => new("$and", new BsonArray(terms));
        BsonDocument Or(params BsonValue[] terms) => new("$or", new BsonArray(terms));
        var completeEffects = new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
            { "input", "$valueEffects" }, { "as", "effect" }, { "in", And(Ne("$$effect.movementId", BsonNull.Value),
                Ne("$$effect.transitionId", BsonNull.Value), Ne("$$effect.transitionHash", BsonNull.Value)) } }) });
        var completeJournal = Or(And(Eq("$admission.finiteValue.total", "0.00"), Eq("$journalId", BsonNull.Value), Eq("$journalFingerprint", BsonNull.Value)),
            And(Ne("$admission.finiteValue.total", "0.00"), Ne("$journalId", BsonNull.Value), Ne("$journalFingerprint", BsonNull.Value)));
        value["$and"].AsBsonArray.Add(new BsonDocument("$expr", And(
            Eq(new BsonDocument("$size", "$valueEffects"), new BsonDocument("$size", "$pickedLots")),
            Eq(new BsonDocument("$size", "$admission.finiteValue.lots"), new BsonDocument("$size", "$pickedLots")),
            Or(Eq("$phase", "preparing"), And(Eq("$postingFence", 1L), completeEffects, completeJournal)))));
        return value;
    }
}

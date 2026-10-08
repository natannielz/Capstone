using MongoDB.Bson;
using UnitToko.Platform.Operations;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using R = UnitToko.Platform.Operations.OperationalAnomalyRecords;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OperationalAnomalySchemas
{
    internal static readonly string[] Collections = [R.Policies, R.PolicyVersions, R.Evaluations, R.Dispositions];
    private static BsonDocument Nullable(BsonDocument value) { value["bsonType"] = new BsonArray { value["bsonType"], "null" }; return value; }
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } };
    private static BsonDocument Money => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0.01m) }, { "maximum", new BsonDecimal128(UnitToko.Modules.Contracts.DomainValues.MaximumMoney) } };
    private static BsonDocument Scope => S.Object(new() { { "fulfillmentOrganizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) } });
    private static BsonDocument Policy => S.Object(new() { { "version", S.Text(120) }, { "approvalReference", S.Text(256) },
        { "maximumPackages", Nullable(S.Number(1, 1000000)) }, { "maximumUnitPrice", Nullable(Money) },
        { "currency", Nullable(new() { { "bsonType", "string" }, { "pattern", "^[A-Z]{3}$" } }) }, { "maximumRefund", Nullable(Money) } });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", name == R.Policies ? S.Text(64) : S.Number(1) },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "scope", Scope } };
        if (name == R.Policies) {
            fields.Add("revision", S.Number(1)); fields.Add("fence", S.Number(0, long.MaxValue - 1));
            fields.Add("policy", Nullable(Policy)); fields.Add("versionId", Nullable(S.Number(1)));
        } else {
            fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("recordedAt", Date);
            fields.Add("requestKey", S.Text(128)); fields.Add("bodyHash", Hash); fields.Add("reason", S.Text(1000)); fields.Add("correlationId", S.Text(128));
            if (name == R.PolicyVersions) {
                fields.Add("sourceKind", S.Choice("order", "refund")); fields.Add("sourceId", S.Number(1));
                fields.Add("expectedRevision", S.Number(1)); fields.Add("revision", S.Number(2)); fields.Add("policy", Policy);
            } else if (name == R.Evaluations) {
                fields.Add("sourceKind", S.Choice("order", "refund")); fields.Add("sourceId", S.Number(1)); fields.Add("sourceHash", Hash);
                fields.Add("ruleVersion", S.Choice(UnitToko.Modules.Operations.OperationalAnomalyRules.Version));
                fields.Add("policyVersion", Nullable(S.Text(120))); fields.Add("policyRevision", S.Number(1)); fields.Add("policyHash", Hash);
                fields.Add("fence", S.Number(0, long.MaxValue - 1));
                fields.Add("signals", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 301 }, { "items", S.Object(new() {
                    { "category", S.Choice("price", "quantity", "refund", "adjustment") }, { "code", S.Text(80) },
                    { "state", S.Choice("consistent", "flagged", "within_policy", "policy_needed", "unavailable") }, { "reference", S.Text(160) },
                    { "observed", Nullable(S.Text(80)) }, { "expected", Nullable(S.Text(80)) }, { "policyVersion", Nullable(S.Text(120)) }, { "explanation", S.Text(1000) } }) } });
            } else if (name == R.Dispositions) {
                fields.Add("evaluationId", S.Number(1)); fields.Add("evaluationHash", Hash);
                fields.Add("outcome", S.Choice("acknowledged", "requires_action", "not_concerning"));
            } else throw new ArgumentOutOfRangeException(nameof(name));
        }
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        R.Policies => [],
        R.PolicyVersions => [("ux_operational_anomaly_policy_revision", new() { { "scope", 1 }, { "revision", 1 } }, true),
            ("ux_operational_anomaly_policy_version", new() { { "scope", 1 }, { "policy.version", 1 } }, true)],
        R.Evaluations => [("ix_operational_anomaly_source", new() { { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 }, { "_id", -1 } }, false),
            ("ux_operational_anomaly_evaluation_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        R.Dispositions => [("ux_operational_anomaly_disposition", new("evaluationId", 1), true),
            ("ux_operational_anomaly_disposition_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}

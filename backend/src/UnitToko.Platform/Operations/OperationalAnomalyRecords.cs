using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Operations;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Operations;

internal static class OperationalAnomalyRecords
{
    internal const string Policies = "operationalAnomalyPolicies", Evaluations = "operationalAnomalyEvaluations", Dispositions = "operationalAnomalyDispositions";
    internal const string PolicyVersions = "operationalAnomalyPolicyVersions", ConfigureOperation = "operations.anomaly.policy.configure";
    internal const string EvaluateOperation = "operations.anomaly.evaluate", ReviewOperation = "operations.anomaly.review";
    internal static string Hash(BsonDocument value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJson())));
    internal static BsonDocument Stable(BsonDocument value) => new(value.Where(field => field.Name != "fence").Select(field => new BsonElement(field.Name, field.Value.DeepClone())));
    internal static DomainValidationException Unavailable() => new("anomaly_source_unavailable", "Anomaly review source or stored evidence cannot be verified");
    internal static DomainValidationException Changed() => new("anomaly_source_changed", "Source or review policy changed; evaluate the current source again");
    internal static BsonDocument Scope(ResourceScope scope) => new() {
        { "fulfillmentOrganizationId", scope.FulfillmentOrganizationId!.Value }, { "shopId", scope.ShopId!.Value }, { "channelId", scope.ChannelId!.Value } };
    internal static string PolicyKey(ResourceScope scope) => $"{scope.FulfillmentOrganizationId}:{scope.ShopId}:{scope.ChannelId}";
    internal static AnomalyReviewPolicy? Policy(BsonDocument row)
    {
        if (row["policy"].IsBsonNull) return null;
        var value = row["policy"].AsBsonDocument;
        var policy = new AnomalyReviewPolicy(value["version"].AsString, value["approvalReference"].AsString,
            value["maximumPackages"].IsBsonNull ? null : value["maximumPackages"].AsInt64,
            value["maximumUnitPrice"].IsBsonNull ? null : Decimal128.ToDecimal(value["maximumUnitPrice"].AsDecimal128),
            value["currency"].IsBsonNull ? null : value["currency"].AsString,
            value["maximumRefund"].IsBsonNull ? null : Decimal128.ToDecimal(value["maximumRefund"].AsDecimal128));
        OperationalAnomalyRules.Validate(policy); return policy;
    }
    internal static BsonDocument Policy(AnomalyReviewPolicy policy) {
        OperationalAnomalyRules.Validate(policy);
        return new() { { "version", policy.Version }, { "approvalReference", policy.ApprovalReference },
            { "maximumPackages", policy.MaximumPackages.HasValue ? new BsonInt64(policy.MaximumPackages.Value) : BsonNull.Value },
            { "maximumUnitPrice", policy.MaximumUnitPrice.HasValue ? new BsonDecimal128(policy.MaximumUnitPrice.Value) : BsonNull.Value },
            { "currency", (BsonValue?)policy.Currency ?? BsonNull.Value },
            { "maximumRefund", policy.MaximumRefund.HasValue ? new BsonDecimal128(policy.MaximumRefund.Value) : BsonNull.Value } };
    }
    internal static BsonDocument Signal(AnomalySignal s) => new() {
        { "category", s.Category }, { "code", s.Code }, { "state", s.State }, { "reference", s.Reference },
        { "observed", (BsonValue?)s.Observed ?? BsonNull.Value }, { "expected", (BsonValue?)s.Expected ?? BsonNull.Value },
        { "policyVersion", (BsonValue?)s.PolicyVersion ?? BsonNull.Value }, { "explanation", s.Explanation } };
    internal static AnomalySignal Signal(BsonValue s) => new(s["category"].AsString, s["code"].AsString, s["state"].AsString, s["reference"].AsString,
        s["observed"].IsBsonNull ? null : s["observed"].AsString, s["expected"].IsBsonNull ? null : s["expected"].AsString,
        s["policyVersion"].IsBsonNull ? null : s["policyVersion"].AsString, s["explanation"].AsString);
    internal static AnomalyDisposition Disposition(BsonDocument row) => new(new(row["_id"].AsInt64), new(row["actorId"].AsInt64),
        new(row["recordedAt"].ToUniversalTime(), TimeSpan.Zero), row["outcome"].AsString, row["reason"].AsString);
    internal static AnomalyEvaluation Evaluation(BsonDocument row, bool current, BsonDocument? disposition) => new(new(row["_id"].AsInt64),
        row["sourceKind"].AsString, new(row["sourceId"].AsInt64), row["sourceHash"].AsString, row["ruleVersion"].AsString,
        row["policyVersion"].IsBsonNull ? null : row["policyVersion"].AsString, new(row["policyRevision"].AsInt64),
        new(row["recordedAt"].ToUniversalTime(), TimeSpan.Zero), row["signals"].AsBsonArray.Select(Signal).ToArray(), current,
        disposition is null ? null : Disposition(disposition));
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in OperationalAnomalySchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            var keys = OperationalAnomalySchemas.Keys(name);
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, OperationalAnomalySchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw Unavailable();
        }
    }
}

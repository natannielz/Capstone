using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal static class ReplenishmentRecords
{
    internal const string Collection = "inventoryReplenishmentEvaluations", Operation = "inventory.replenishment.evaluate";
    internal static DomainValidationException Invalid() => new("replenishment_evidence_unavailable", "The saved replenishment review or its provenance cannot be verified");
    internal static BsonValue Number(long? value) => value.HasValue ? new BsonInt64(value.Value) : BsonNull.Value;
    internal static long? Number(BsonValue value) => value.IsBsonNull ? null : value.AsInt64;
    internal static BsonDocument Inputs(ReplenishmentInputs value) => new() {
        { "dailyDemandPerDay", value.DailyDemandPerDay.HasValue ? new BsonDecimal128(value.DailyDemandPerDay.Value) : BsonNull.Value },
        { "leadTimeDays", value.LeadTimeDays.HasValue ? new BsonInt32(value.LeadTimeDays.Value) : BsonNull.Value },
        { "safetyStockPackages", Number(value.SafetyStockPackages) }, { "reorderPointPackages", Number(value.ReorderPointPackages) },
        { "purchaseMinimumPackages", value.PurchaseMinimumPackages }, { "purchaseIncrementPackages", value.PurchaseIncrementPackages } };
    internal static ReplenishmentInputs Inputs(BsonValue value) => new(value["dailyDemandPerDay"].IsBsonNull ? null : Decimal128.ToDecimal(value["dailyDemandPerDay"].AsDecimal128),
        value["leadTimeDays"].IsBsonNull ? null : value["leadTimeDays"].AsInt32, Number(value["safetyStockPackages"]), Number(value["reorderPointPackages"]),
        value["purchaseMinimumPackages"].AsInt64, value["purchaseIncrementPackages"].AsInt64);
    internal static BsonDocument Input(ReplenishmentReviewInput value) => new() {
        { "location", CatalogRepository.Owner(value.Location) }, { "poolId", value.PoolId }, { "expectedPoolRevision", value.ExpectedPoolRevision },
        { "skuId", value.SkuId }, { "expectedSkuRevision", value.ExpectedSkuRevision }, { "definitionId", value.DefinitionId },
        { "inputs", Inputs(value.Inputs) }, { "inputReference", value.InputReference } };
    internal static ReplenishmentReviewInput Input(BsonValue value) => new(CatalogRepository.Owner(value["location"].AsBsonDocument),
        value["poolId"].AsInt64, value["expectedPoolRevision"].AsInt64, value["skuId"].AsInt64, value["expectedSkuRevision"].AsInt64,
        value["definitionId"].AsInt64, Inputs(value["inputs"]), value["inputReference"].AsString);
    internal static BsonDocument Stock(ReplenishmentStock value) => new() { { "currentAuthority", value.CurrentAuthority },
        { "usableBeforeReservations", Number(value.UsableBeforeReservations) }, { "reservedPackages", Number(value.ReservedPackages) },
        { "additionalUnreservedCommitments", Number(value.AdditionalUnreservedCommitments) } };
    internal static ReplenishmentStock Stock(BsonValue value) => new(value["currentAuthority"].AsBoolean, Number(value["usableBeforeReservations"]),
        Number(value["reservedPackages"]), Number(value["additionalUnreservedCommitments"]));
    internal static BsonDocument Supply(ReplenishmentSupply value) => new() { { "reference", value.Reference }, { "packages", value.Packages },
        { "expectedAt", value.ExpectedAt.HasValue ? new BsonDateTime(value.ExpectedAt.Value.UtcDateTime) : BsonNull.Value }, { "verified", value.Verified } };
    internal static ReplenishmentSupply Supply(BsonValue value) => new(value["reference"].AsString, value["packages"].AsInt64,
        value["expectedAt"].IsBsonNull ? null : Time(value["expectedAt"]), value["verified"].AsBoolean);
    internal static BsonDocument Advice(ReplenishmentAdvice value) => new() {
        { "ruleVersion", value.RuleVersion }, { "observedAt", new BsonDateTime(value.ObservedAt.UtcDateTime) },
        { "horizon", value.Horizon.HasValue ? new BsonDateTime(value.Horizon.Value.UtcDateTime) : BsonNull.Value },
        { "availableAfterCommitments", Number(value.AvailableAfterCommitments) }, { "thresholdAlert", value.ThresholdAlert.HasValue ? new BsonBoolean(value.ThresholdAlert.Value) : BsonNull.Value },
        { "eligibleSupplyPackages", Number(value.EligibleSupplyPackages) }, { "inventoryPosition", Number(value.InventoryPosition) },
        { "requiredStockPackages", Number(value.RequiredStockPackages) }, { "suggestedPurchasePackages", Number(value.SuggestedPurchasePackages) },
        { "warnings", new BsonArray(value.Warnings) }, { "supplyDecisions", new BsonArray(value.SupplyDecisions.Select(item => new BsonDocument {
            { "reference", item.Reference }, { "state", item.State } })) } };
    internal static string Hash(BsonDocument value) => CatalogRepository.Digest(value);
    internal static DateTimeOffset Time(BsonValue value) => new(value.AsBsonDateTime.ToUniversalTime(), TimeSpan.Zero);
    internal static BsonDocument Result(BsonDocument row) => new() { { "evaluationId", row["_id"] }, { "sourceHash", row["sourceHash"] }, { "evaluationHash", Hash(row) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation },
        { "operation", Operation }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "createdAt", row["recordedAt"] }, { "record", row.DeepClone() } };
    internal static BsonDocument Create(long id, long auditId, ActorContext actor, ReplenishmentReviewInput input,
        StockCountDefinitionSnapshot definition, ReplenishmentStock stock, bool coverage, IReadOnlyList<ReplenishmentSupply> supplies,
        ImmutableArray<string> sourceWarnings, BsonDocument provenance, AuthorizationCommandMetadata metadata, DateTimeOffset at)
    {
        ReplenishmentReviewPolicy.Validate(input);
        at = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
        var advice = Evaluate(input, stock, coverage, supplies, sourceWarnings, at);
        return new() { { "_id", id }, { "schemaVersion", 1 }, { "kind", Operation }, { "input", Input(input) },
            { "definition", StockCountBson.Definition(definition) }, { "stock", Stock(stock) }, { "supplyCoverageComplete", coverage },
            { "supplies", new BsonArray(supplies.Select(Supply)) }, { "sourceWarnings", new BsonArray(sourceWarnings) },
            { "provenance", provenance.DeepClone() }, { "sourceHash", Hash(provenance) }, { "advice", Advice(advice) },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "issuer", actor.ProviderIssuer }, { "subject", actor.ProviderSubject },
            { "recordedAt", new BsonDateTime(at.UtcDateTime) }, { "requestKey", metadata.IdempotencyKey },
            { "bodyHash", AuthorizationStore.CommandHash(input, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
    }
    internal static ReplenishmentEvaluation Read(BsonDocument row)
    {
        var input = Input(row["input"]); ReplenishmentReviewPolicy.Validate(input);
        var raw = row["definition"]; var definition = new StockCountDefinitionSnapshot(CatalogRepository.Owner(raw["owner"].AsBsonDocument),
            raw["skuId"].AsInt64, raw["skuRevision"].AsInt64, raw["familyId"].AsInt64, raw["familyRevision"].AsInt64,
            raw["categoryId"].AsInt64, raw["categoryRevision"].AsInt64, raw["definitionId"].AsInt64, raw["definitionVersion"].AsInt64,
            raw["skuCode"].AsString, raw["physicalItemKey"].AsString, raw["packageLabel"].AsString, raw["packageKind"].AsString,
            raw["baseUnitCode"].AsString, raw["baseUnitsPerPackage"].AsInt32);
        var stock = Stock(row["stock"]); var supplies = row["supplies"].AsBsonArray.Select(Supply).ToImmutableArray();
        var warnings = row["sourceWarnings"].AsBsonArray.Select(value => value.AsString).ToImmutableArray();
        var advice = Evaluate(input, stock, row["supplyCoverageComplete"].AsBoolean, supplies, warnings, Time(row["recordedAt"]));
        if (row["kind"] != Operation || row["schemaVersion"] != 1 || definition.SkuId != input.SkuId || definition.SkuRevision != input.ExpectedSkuRevision ||
            definition.DefinitionId != input.DefinitionId || !ScopePolicy.ContainsScope(definition.Owner, input.Location) ||
            row["sourceHash"] != Hash(row["provenance"].AsBsonDocument) || row["advice"] != Advice(advice) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(input, row["reason"].AsString)) throw Invalid();
        return new(row["_id"].AsInt64, input, definition, stock, advice, supplies, row["sourceHash"].AsString, row["reason"].AsString, row["actorId"].AsInt64);
    }
    private static ReplenishmentAdvice Evaluate(ReplenishmentReviewInput input, ReplenishmentStock stock, bool coverage,
        IReadOnlyList<ReplenishmentSupply> supplies, ImmutableArray<string> warnings, DateTimeOffset at)
    {
        var advice = ReplenishmentPolicy.Evaluate(input.Inputs, stock, coverage, supplies, at);
        return advice with { Warnings = [.. advice.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal)] };
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct);
        var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(Collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        var keys = ReplenishmentSchemas.Keys;
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, ReplenishmentSchemas.Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw Invalid();
    }
}

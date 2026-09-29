using System.Globalization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
namespace UnitToko.Api.Inventory;

// Explicit projections: stored identity, person, audit and receipt fields never leave this boundary.
internal static class StockSourcePolicyResponses
{
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static string Status(BsonValue? value) => value is null ? "proposed" : value.AsString switch
        { "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw StockSourceSetupInputs.Invalid() };
    private static LongId? OptionalId(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && !value.IsBsonNull ? new(value.AsInt64) : null;
    private static string? OptionalNumber(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && !value.IsBsonNull ? Number(value) : null;
    private static AccessScopeDto Location(BsonValue value)
    {
        var row = value.AsBsonDocument;
        return AccessScopeDto.From(new StockLocation(new(row["organizationId"].AsInt64), new(row["shopId"].AsInt64),
            new(row["warehouseId"].AsInt64), new(row["channelId"].AsInt64)).ToScope());
    }
    private static StockSourcePolicyConfigurationDto Configuration(BsonDocument row)
    {
        var source = row["source"].AsBsonDocument;
        return new(StockSourceRevisionResponses.Settings(source["input"].AsBsonDocument), source["active"].AsBoolean,
            Time(source["approvedAt"]), new(source["creationProposalId"].AsInt64), OptionalId(source, "revisionProposalId"),
            [.. row["mappings"].AsBsonArray.Select(value => new StockSourcePolicyMappingDto(new(value["aliasId"].AsInt64),
                Number(value["aliasRevision"]), Number(value["poolRevision"]), Location(value["requestedLocation"]),
                value["enabled"].AsBoolean, new(value["proposalId"].AsInt64), new(value["decisionId"].AsInt64)))]);
    }
    private static StockSourcePolicyInputDto Input(BsonDocument row) => new(row["writer"].AsString,
        row["externalTenant"].IsBsonNull ? null : row["externalTenant"].AsString, Number(row["feedIntervalMilliseconds"]),
        row["reconciliationProcedureReference"].AsString, row["outageProcedureReference"].AsString);
    internal static StockSourcePolicyPreviewDto Preview(BsonDocument row) => new(new(row["poolId"].AsInt64), Number(row["poolRevision"]),
        Number(row["suspensionRevision"]), Number(row["policyRevision"]), row["configurationHash"].AsString,
        Configuration(row["configuration"].AsBsonDocument));
    internal static StockSourcePolicyCommandDto Command(BsonDocument row) => new(new(row["proposalId"].AsInt64), "1",
        Status(row.TryGetValue("status", out var status) ? status : null), new(row["poolId"].AsInt64),
        OptionalNumber(row, "basePolicyRevision"), OptionalId(row, "decisionId"), OptionalId(row, "policyId"), OptionalNumber(row, "policyRevision"));
    internal static StockSourcePolicyDetailDto Detail(BsonDocument view)
    {
        var proposal = view["proposal"].AsBsonDocument;
        var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        return new(new(proposal["_id"].AsInt64), Number(proposal["revision"]), Status(decision?["decision"]), new(proposal["poolId"].AsInt64),
            Number(proposal["poolRevision"]), Number(proposal["suspensionRevision"]), Number(proposal["basePolicyRevision"]),
            proposal["configurationHash"].AsString, Configuration(proposal["configuration"].AsBsonDocument), Input(proposal["input"].AsBsonDocument),
            Time(proposal["proposedAt"]), proposal["reason"].AsString,
            decision is null ? null : new(Status(decision["decision"]), Time(decision["decidedAt"]), decision["reviewReference"].AsString, decision["reason"].AsString),
            decision is null ? null : new(decision["_id"].AsInt64), decision is null ? null : OptionalId(decision, "policyId"),
            decision is null ? null : OptionalNumber(decision, "policyRevision"));
    }
    internal static StockSourcePolicyReviewDto Review(BsonDocument row)
    {
        var current = row["current"].AsBsonDocument;
        return new(row["action"].AsString, row["bindingCurrent"].AsBoolean,
            new(Number(current["poolRevision"]), Number(current["suspensionRevision"]), Number(current["policyRevision"]), current["configurationHash"].AsString),
            Detail(row));
    }
    internal static StockSourcePolicyLatestDto Latest(BsonDocument row) =>
        new(row["bindingCurrent"].AsBoolean, row["proposal"].IsBsonNull ? null : Detail(row));
}
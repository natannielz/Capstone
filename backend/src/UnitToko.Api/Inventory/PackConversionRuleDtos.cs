using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionPackageQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, [property: JsonRequired] string Role,
    string? Search = null, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionPageQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionRuleInputDto([property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId SourceSkuId, [property: JsonRequired] LongId SourceSkuRevision,
    [property: JsonRequired] LongId SourceDefinitionId, [property: JsonRequired] LongId DestinationSkuId,
    [property: JsonRequired] LongId DestinationSkuRevision, [property: JsonRequired] LongId DestinationDefinitionId,
    [property: JsonRequired] int SourcePackagesPerStep, [property: JsonRequired] int DestinationPackagesPerStep)
{
    internal PackConversionRuleInput ToDomain() => PackConversionRulePolicy.Copy(new PackConversionRuleInput(StockCountInputs.Location(Location),
        SourceSkuId.Value, SourceSkuRevision.Value, SourceDefinitionId.Value, DestinationSkuId.Value,
        DestinationSkuRevision.Value, DestinationDefinitionId.Value, SourcePackagesPerStep, DestinationPackagesPerStep));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionPreviewQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] PackConversionRuleInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionProposeDto([property: JsonRequired] PackConversionRuleInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionReviewQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Action)
{
    internal PackConversionRuleDecision Decision() => Action switch {
        "approve" => PackConversionRuleDecision.Approve, "reject" => PackConversionRuleDecision.Reject,
        _ => throw PackConversionRulePolicy.Invalid()
    };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionDecideDto([property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionReceiptQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionAdmissionQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] LongId RuleId, [property: JsonRequired] LongId RuleVersion,
    [property: JsonRequired] LongId PoolId, [property: JsonRequired] LongId PoolRevision,
    [property: JsonRequired] LongId SourceBalanceId, [property: JsonRequired] LongId SourceBalanceRevision,
    [property: JsonRequired] int Steps)
{
    internal PackConversionAdmissionInput ToDomain() => PackConversionRulePolicy.Copy(new PackConversionAdmissionInput(RuleId.Value, RuleVersion.Value,
        PoolId.Value, PoolRevision.Value, SourceBalanceId.Value, SourceBalanceRevision.Value, Steps));
}

public sealed record PackConversionPreviewDto(PackConversionRuleInputDto Input, StockCountPackageOptionDto Source,
    StockCountPackageOptionDto Destination, string Binding, string SourcePackages, string DestinationPackages,
    string BaseUnits, bool PhysicalConversionAvailable, DateTimeOffset ObservedAt);
public sealed record PackConversionCommandDto(LongId ProposalId, string Revision, string State,
    LongId? DecisionId, LongId? RuleId, bool PhysicalConversionAvailable);
public sealed record PackConversionDecisionDto(LongId Id, string Action, DateTimeOffset DecidedAt, string Reason);
public sealed record PackConversionDetailDto(LongId Id, string Revision, string Status, PackConversionRuleInputDto Input,
    StockCountPackageOptionDto Source, StockCountPackageOptionDto Destination, DateTimeOffset ProposedAt, string Reason,
    PackConversionDecisionDto? Decision, bool CanReview, bool PhysicalConversionAvailable);
public sealed record PackConversionReviewDto(PackConversionDetailDto Proposal, string Action, string Binding,
    bool PhysicalConversionAvailable);
public sealed record PackConversionProposalPageDto(PackConversionDetailDto[] Items, LongId? NextProposalId,
    bool PhysicalConversionAvailable);
public sealed record PackConversionRuleDto(LongId Id, string Version, LongId ProposalId, LongId DecisionId,
    PackConversionRuleInputDto Input, StockCountPackageOptionDto Source, StockCountPackageOptionDto Destination,
    DateTimeOffset ApprovedAt, bool PhysicalConversionAvailable);
public sealed record PackConversionRulePageDto(PackConversionRuleDto[] Items, LongId? NextRuleId,
    bool PhysicalConversionAvailable);
public sealed record PackConversionReceiptDto(string State, PackConversionCommandDto? Result);
public sealed record PackConversionAdmissionDto(LongId RuleId, string RuleVersion, string State,
    string SourcePackages, string DestinationPackages, string BaseUnits, string AvailableSourcePackages,
    string[] BlockingReasons, bool PhysicalConversionAvailable);

// Public projections deliberately omit private person proofs, audit fields and authoring ownership.
internal static class PackConversionResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static LongId? OptionalId(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && !value.IsBsonNull ? new(value.AsInt64) : null;
    private static PackConversionRuleInputDto Input(BsonDocument row) {
        var location = row["location"].AsBsonDocument;
        return new(AccessScopeDto.From(new StockLocation(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)).ToScope()),
            new(row["sourceSkuId"].AsInt64), new(row["sourceSkuRevision"].AsInt64), new(row["sourceDefinitionId"].AsInt64),
            new(row["destinationSkuId"].AsInt64), new(row["destinationSkuRevision"].AsInt64), new(row["destinationDefinitionId"].AsInt64),
            row["sourcePackagesPerStep"].AsInt32, row["destinationPackagesPerStep"].AsInt32);
    }
    private static StockCountPackageOptionDto Package(BsonValue value) {
        var row = value.AsBsonDocument; var sku = row["sku"]; var definition = row["definition"];
        return new(new(sku["_id"].AsInt64), Number(sku["revision"].AsInt64), new(definition["_id"].AsInt64),
            Number(definition["version"].AsInt64), sku["sku"].AsString, row["family"]["name"].AsString,
            definition["packageLabel"].AsString, definition["packageKind"].AsString,
            definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32);
    }
    internal static PackConversionPreviewDto Preview(BsonDocument row) => new(Input(row["input"].AsBsonDocument),
        Package(row["catalog"]["source"]), Package(row["catalog"]["destination"]), row["binding"].AsString,
        Number(row["sourcePackages"].AsInt64), Number(row["destinationPackages"].AsInt64), Number(row["baseUnits"].AsInt64), false, Time(row["observedAt"]));
    internal static PackConversionCommandDto Command(BsonDocument row) => new(new(row["proposalId"].AsInt64),
        Number(row["revision"].AsInt64), row["state"].AsString, OptionalId(row, "decisionId"), OptionalId(row, "ruleId"), false);
    internal static PackConversionDetailDto Detail(BsonDocument row) => Detail(row["proposal"].AsBsonDocument,
        row["decision"].IsBsonNull ? null : row["decision"].AsBsonDocument, row["canReview"].AsBoolean);
    private static PackConversionDetailDto Detail(BsonDocument proposal, BsonDocument? decision, bool canReview) => new(new(proposal["_id"].AsInt64),
        Number(proposal["revision"].AsInt64), decision is null ? "pending" : decision["decision"] == "approve" ? "approved" : "rejected",
        Input(proposal["input"].AsBsonDocument), Package(proposal["catalog"]["source"]), Package(proposal["catalog"]["destination"]),
        Time(proposal["recordedAt"]), proposal["reason"].AsString, decision is null ? null :
            new(new(decision["_id"].AsInt64), decision["decision"].AsString, Time(decision["recordedAt"]), decision["reason"].AsString), canReview, false);
    internal static PackConversionReviewDto Review(BsonDocument row) => new(Detail(row["proposal"].AsBsonDocument, null, row["canReview"].AsBoolean),
        row["decision"].AsString, row["binding"].AsString, false);
    internal static PackConversionProposalPageDto Proposals(BsonDocument row) =>
        new([.. row["items"].AsBsonArray.Select(value => Detail(value.AsBsonDocument))], OptionalId(row, "nextProposalId"), false);
    internal static PackConversionRulePageDto Rules(BsonDocument row) => new([.. row["items"].AsBsonArray.Select(value => {
        var rule = value.AsBsonDocument;
        return new PackConversionRuleDto(new(rule["_id"].AsInt64), Number(rule["version"].AsInt64), new(rule["proposalId"].AsInt64),
            new(rule["decisionId"].AsInt64), Input(rule["input"].AsBsonDocument), Package(rule["catalog"]["source"]),
            Package(rule["catalog"]["destination"]), Time(rule["recordedAt"]), false);
    })], OptionalId(row, "nextRuleId"), false);
    internal static PackConversionAdmissionDto Admission(PackConversionAdmission row) => new(new(row.RuleId), Number(row.RuleVersion),
        row.State, Number(row.Quantities.SourcePackages), Number(row.Quantities.DestinationPackages), Number(row.Quantities.BaseUnits),
        Number(row.AvailableSourcePackages), [.. row.BlockingReasons], false);
}

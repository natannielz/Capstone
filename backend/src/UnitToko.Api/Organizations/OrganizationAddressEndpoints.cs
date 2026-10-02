using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Organizations;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Organizations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressTargetDto([property: JsonRequired] ScopeNodeKind Kind, [property: JsonRequired] LongId Id);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostalAddressDto([property: JsonRequired] string Line1, [property: JsonRequired] string? Line2,
    [property: JsonRequired] string City, [property: JsonRequired] string Region, [property: JsonRequired] string PostalCode,
    [property: JsonRequired] string CountryCode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressInputDto([property: JsonRequired] OrganizationAddressTargetDto Target, [property: JsonRequired] string Purpose,
    [property: JsonRequired] string ExpectedScopeVersion, [property: JsonRequired] string ExpectedProfileVersion,
    [property: JsonRequired] string ExpectedAddressVersion, [property: JsonRequired] PostalAddressDto Address) {
    internal OrganizationAddressInput Domain() {
        if (Target is null || Address is null) throw OrganizationAddressPolicy.Invalid();
        return OrganizationAddressPolicy.Copy(new OrganizationAddressInput(new(Target.Kind, Target.Id.Value), OrganizationAddressEndpoints.Purpose(Purpose),
            OrganizationAddressEndpoints.Version(ExpectedScopeVersion), OrganizationAddressEndpoints.Version(ExpectedProfileVersion),
            OrganizationAddressEndpoints.Version(ExpectedAddressVersion), new(Address.Line1, Address.Line2, Address.City, Address.Region, Address.PostalCode, Address.CountryCode)));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressTargetsQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressQueryDto([property: JsonRequired] OrganizationAddressTargetDto Target, [property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressProposalsQueryDto([property: JsonRequired] OrganizationAddressTargetDto Target, [property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressProposeDto([property: JsonRequired] OrganizationAddressInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressDecisionPreviewRequestDto([property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressReadQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationAddressRecoveryQueryDto([property: JsonRequired] string Operation, [property: JsonRequired] string Key, [property: JsonRequired] string Purpose);
public sealed record OrganizationAddressTargetChoiceDto(OrganizationAddressTargetDto Target, string Purpose, string Code, string Name,
    string ScopeVersion, string ProfileVersion, string AddressVersion, LongId? AddressId, LongId? VersionId, PostalAddressDto? CurrentAddress);
public sealed record OrganizationAddressTargetPageDto(OrganizationAddressTargetChoiceDto[] Items, bool HasMore);
public sealed record OrganizationAddressPreviewViewDto(OrganizationAddressTargetDto Target, string Purpose, string Code, string Name,
    string ScopeVersion, string ProfileVersion, string AddressVersion, PostalAddressDto Address, string SourceHash);
public sealed record OrganizationAddressPreviewDto(string Binding, OrganizationAddressPreviewViewDto View);
public sealed record OrganizationAddressCurrentDto(LongId? AddressId, OrganizationAddressTargetDto Target, string Purpose, string Version,
    LongId? VersionId, string Code, string Name, PostalAddressDto? Address, DateTimeOffset? ApprovedAt, bool Current);
public sealed record OrganizationAddressProposalDto(LongId ProposalId, LongId AddressId, string Revision, OrganizationAddressTargetDto Target,
    string Purpose, string Code, string Name, PostalAddressDto Address, string BaseVersion, DateTimeOffset ProposedAt, string State, string? Decision,
    string? AddressVersion, LongId? VersionId, bool BindingCurrent);
public sealed record OrganizationAddressProposalPageDto(OrganizationAddressProposalDto[] Items, LongId? NextProposalId);
public sealed record OrganizationAddressDecisionViewDto(LongId ProposalId, string Revision, LongId AddressId, OrganizationAddressTargetDto Target,
    string Purpose, string Decision, PostalAddressDto Address, string Code, string Name, string BaseVersion, bool BindingCurrent);
public sealed record OrganizationAddressDecisionPreviewDto(string Binding, OrganizationAddressDecisionViewDto View);
public sealed record OrganizationAddressResultDto(LongId AddressId, LongId ProposalId, LongId RecordId, string Operation, string? AddressVersion,
    LongId? VersionId, DateTimeOffset RecordedAt);
public sealed record OrganizationAddressRecoveryDto(string Status, OrganizationAddressResultDto? Result);

public static class OrganizationAddressEndpoints {
    public static void MapOrganizationAddressEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/organizations/addresses").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (OrganizationAddressTargetsQueryDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            A.Private(http); var row = await service.TargetsAsync(http.Features.Get<ResolvedActor>()!.Actor, Purpose(request.Purpose), ct);
            return TypedResults.Ok(new OrganizationAddressTargetPageDto(row["items"].AsBsonArray.Select(value => Choice(value.AsBsonDocument)).ToArray(), row["hasMore"].AsBoolean));
        }).WithName("OrganizationAddressTargets");
        group.MapPost("/preview", async (OrganizationAddressInputDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            A.Private(http); var preview = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Domain(), ct);
            return TypedResults.Ok(new OrganizationAddressPreviewDto(preview.Binding, PreviewView(preview.View)));
        }).WithName("PreviewOrganizationAddress");
        group.MapPost("/proposals", async (OrganizationAddressProposeDto request, HttpContext http, OrganizationAddressService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw OrganizationAddressPolicy.Invalid(); A.Private(http);
            var result = await service.ProposeAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(result));
        }).WithName("ProposeOrganizationAddress");
        group.MapPost("/query", async (OrganizationAddressQueryDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            A.Private(http); return TypedResults.Ok(Current(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, Target(request.Target), Purpose(request.Purpose), ct)));
        }).WithName("ReadOrganizationAddress");
        group.MapPost("/proposals/query", async (OrganizationAddressProposalsQueryDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            A.Private(http); var row = await service.ListProposalsAsync(http.Features.Get<ResolvedActor>()!.Actor, Target(request.Target), Purpose(request.Purpose), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrganizationAddressProposalPageDto(row["items"].AsBsonArray.Select(value => Proposal(value.AsBsonDocument)).ToArray(), Id(row["nextProposalId"])));
        }).WithName("OrganizationAddressProposals");
        group.MapPost("/proposals/{id}/query", async (string id, OrganizationAddressReadQueryDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            if (request.Purpose != "organization-address") throw OrganizationAddressPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(Proposal(await service.ReadProposalAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadOrganizationAddressProposal");
        group.MapPost("/proposals/{id}/preview-decision", async (string id, OrganizationAddressDecisionPreviewRequestDto request, HttpContext http, OrganizationAddressService service,
            [FromHeader(Name = "If-Match")] string expectedRevision, CancellationToken ct) => {
            A.Private(http); var preview = await service.PreviewDecisionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), ct);
            return TypedResults.Ok(new OrganizationAddressDecisionPreviewDto(preview.Binding, DecisionView(preview.View)));
        }).WithName("PreviewOrganizationAddressDecision");
        group.MapPost("/proposals/{id}/decide", async (string id, OrganizationAddressDecideDto request, HttpContext http, OrganizationAddressService service,
            [FromHeader(Name = "If-Match")] string expectedRevision, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            A.Private(http); return TypedResults.Ok(Result(await service.DecideAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideOrganizationAddress");
        group.MapPost("/operations/query", async (OrganizationAddressRecoveryQueryDto request, HttpContext http, OrganizationAddressService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw OrganizationAddressPolicy.Invalid(); A.Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Operation, request.Key, ct);
            return TypedResults.Ok(new OrganizationAddressRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverOrganizationAddress");
    }
    internal static OrganizationAddressPurpose Purpose(string value) => value switch { "legal_billing" => OrganizationAddressPurpose.LegalBilling,
        "delivery" => OrganizationAddressPurpose.Delivery, "dispatch" => OrganizationAddressPurpose.Dispatch, _ => throw OrganizationAddressPolicy.Invalid() };
    private static OrganizationAddressDecision Decision(string value) => value switch { "approve" => OrganizationAddressDecision.Approve, "reject" => OrganizationAddressDecision.Reject, _ => throw OrganizationAddressPolicy.Invalid() };
    internal static long Version(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0 || version >= long.MaxValue || value != version.ToString(CultureInfo.InvariantCulture)) throw OrganizationAddressPolicy.Invalid();
        return version;
    }
    private static OrganizationAddressTarget Target(OrganizationAddressTargetDto target) => target is null ? throw OrganizationAddressPolicy.Invalid() : new(target.Kind, target.Id.Value);
    private static OrganizationAddressTargetDto Target(BsonValue row) => new(Enum.Parse<ScopeNodeKind>(row["kind"].AsString, true), new(row["id"].AsInt64));
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Number(BsonValue value) => value.IsBsonNull ? null : A.Number(value.AsInt64);
    private static DateTimeOffset? At(BsonValue value) => value.IsBsonNull ? null : new(value.ToUniversalTime());
    private static PostalAddressDto Postal(BsonValue row) => new(row["line1"].AsString, row["line2"].IsBsonNull ? null : row["line2"].AsString,
        row["city"].AsString, row["region"].AsString, row["postalCode"].AsString, row["countryCode"].AsString);
    private static OrganizationAddressTargetChoiceDto Choice(BsonDocument row) => new(Target(row["target"]), row["purpose"].AsString, row["code"].AsString, row["name"].AsString,
        A.Number(row["scopeVersion"].AsInt64), A.Number(row["profileVersion"].AsInt64), A.Number(row["addressVersion"].AsInt64), Id(row["addressId"]), Id(row["versionId"]), row["currentAddress"].IsBsonNull ? null : Postal(row["currentAddress"]));
    private static OrganizationAddressPreviewViewDto PreviewView(BsonDocument row) => new(Target(row["target"]), row["purpose"].AsString, row["code"].AsString, row["name"].AsString,
        A.Number(row["scopeVersion"].AsInt64), A.Number(row["profileVersion"].AsInt64), A.Number(row["addressVersion"].AsInt64), Postal(row["address"]), row["sourceHash"].AsString);
    private static OrganizationAddressCurrentDto Current(BsonDocument row) => new(Id(row["addressId"]), Target(row["target"]), row["purpose"].AsString,
        A.Number(row["version"].AsInt64), Id(row["versionId"]), row["code"].AsString, row["name"].AsString, row["address"].IsBsonNull ? null : Postal(row["address"]), At(row["approvedAt"]), row["current"].AsBoolean);
    private static OrganizationAddressProposalDto Proposal(BsonDocument row) => new(new(row["proposalId"].AsInt64), new(row["addressId"].AsInt64), A.Number(row["revision"].AsInt64), Target(row["target"]),
        row["purpose"].AsString, row["code"].AsString, row["name"].AsString, Postal(row["address"]), A.Number(row["baseVersion"].AsInt64), new(row["proposedAt"].ToUniversalTime()), row["state"].AsString,
        row["decision"].IsBsonNull ? null : row["decision"].AsString, Number(row["addressVersion"]), Id(row["versionId"]), row["bindingCurrent"].AsBoolean);
    private static OrganizationAddressDecisionViewDto DecisionView(BsonDocument row) => new(new(row["proposalId"].AsInt64), A.Number(row["revision"].AsInt64), new(row["addressId"].AsInt64), Target(row["target"]),
        row["purpose"].AsString, row["decision"].AsString, Postal(row["address"]), row["code"].AsString, row["name"].AsString, A.Number(row["baseVersion"].AsInt64), row["bindingCurrent"].AsBoolean);
    private static OrganizationAddressResultDto Result(BsonDocument row) => new(new(row["addressId"].AsInt64), new(row["proposalId"].AsInt64), new(row["recordId"].AsInt64), row["operation"].AsString,
        Number(row["addressVersion"]), Id(row["versionId"]), new(row["recordedAt"].ToUniversalTime()));
}

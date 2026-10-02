using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierOwnerDto([property: JsonRequired] LongId OrganizationId,
    [property: JsonRequired] LongId ShopId, [property: JsonRequired] LongId ChannelId) {
    internal SupplierOwner Domain() => SupplierRules.Owner(new(OrganizationId.Value, ShopId.Value, ChannelId.Value));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDetailsDto([property: JsonRequired] string SubjectReference, [property: JsonRequired] string LegalName,
    [property: JsonRequired] string DisplayName, [property: JsonRequired] string? RegistrationNamespace,
    [property: JsonRequired] string? RegistrationValue, [property: JsonRequired] string Address,
    [property: JsonRequired] string ContactName, [property: JsonRequired] string ContactMethod, [property: JsonRequired] string ContactValue) {
    internal SupplierDetails Domain() => SupplierRules.Details(new(SubjectReference, LegalName, DisplayName, RegistrationNamespace,
        RegistrationValue, Address, ContactName, ContactMethod, ContactValue));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierMappingInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] string SourceSystem, [property: JsonRequired] string SourceAccountReference,
    [property: JsonRequired] string ExternalItemCode, [property: JsonRequired] LongId SkuId,
    [property: JsonRequired] string ExpectedSkuRevision, [property: JsonRequired] LongId DefinitionId,
    [property: JsonRequired] string DefinitionVersion, [property: JsonRequired] DateTimeOffset ValidFrom,
    [property: JsonRequired] DateTimeOffset ValidUntil) {
    internal SupplierMappingInput Domain() => new(Location is null ? throw SupplierRules.Invalid() : Location.Domain(),
        SourceSystem, SourceAccountReference, ExternalItemCode, SkuId.Value, SupplierChangeInputDto.Version(ExpectedSkuRevision),
        DefinitionId.Value, SupplierChangeInputDto.Version(DefinitionVersion), ValidFrom, ValidUntil);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierChangeInputDto([property: JsonRequired] SupplierOwnerDto Owner,
    [property: JsonRequired] LongId? SupplierId, [property: JsonRequired] string BaseVersion,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] SupplierDetailsDto Details, [property: JsonRequired] SupplierMappingInputDto[] Mappings) {
    internal SupplierChangeInput Domain() {
        if (Owner is null || Details is null || Mappings is not { Length: > 0 and <= 100 } || Mappings.Any(mapping => mapping is null)) throw SupplierRules.Invalid();
        return SupplierRules.Copy(new(Owner.Domain(), SupplierId?.Value, Version(BaseVersion, true), PolicyId.Value,
            Version(PolicyRevision), Details.Domain(), [.. Mappings.Select(mapping => mapping.Domain())]));
    }
    internal static long Version(string value, bool allowZero = false) {
        if (value is not { Length: > 0 and <= 19 } || value.Length > 1 && value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < (allowZero ? 0 : 1)) throw SupplierRules.Invalid();
        return version;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierOwnerQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    [property: JsonRequired] SupplierOwnerDto Owner);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierPackageQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    [property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] int Offset,
    [property: JsonRequired] int Limit, string? Search = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] SupplierChangeInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierProposeDto([property: JsonRequired] SupplierChangeInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    [property: JsonRequired] SupplierOwnerDto Owner, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierCurrentInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    [property: JsonRequired] LongId SupplierId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierObserveDto([property: JsonRequired] SupplierDetailsDto Observed, [property: JsonRequired] LongId PolicyId,
    [property: JsonRequired] string PolicyRevision, [property: JsonRequired] string TrustedChannelReference,
    [property: JsonRequired] string SourceReference, [property: JsonRequired] bool Attested, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDecisionPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision, [property: JsonRequired] LongId? ObservationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDecisionInputDto([property: JsonRequired] string Decision, [property: JsonRequired] LongId? ObservationId,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierChoicesInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);

public sealed record SupplierSourceDto(ProcurementLabelDto Organization, ProcurementLabelDto Shop, ProcurementLabelDto Channel);
public sealed record SupplierTargetDto(SupplierOwnerDto Owner, ProcurementLabelDto Organization, ProcurementLabelDto Shop, ProcurementLabelDto Channel);
public sealed record SupplierTargetsDto(SupplierTargetDto[] Items);
public sealed record SupplierLocationsDto(ProcurementTargetDto[] Items);
public sealed record SupplierPolicyDto(LongId Id, string Revision, SupplierOwnerDto Owner, string SubjectReference, string LegalName,
    string? RegistrationNamespace, string? RegistrationValue, string Method, string TrustedChannelReference,
    string OperatingOwnerReference, string ApprovalReference, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt,
    string MaxObservationAgeSeconds, bool AllowReferenceOnly, bool Active);
public sealed record SupplierPoliciesDto(SupplierPolicyDto[] Items);
public sealed record SupplierMappingSourceDto(ProcurementLocationDto Location, ProcurementLabelDto Organization,
    ProcurementLabelDto Shop, ProcurementLabelDto Warehouse, ProcurementLabelDto Channel, ProcurementPackageDto Package);
public sealed record SupplierPreviewDto(SupplierChangeInputDto Input, SupplierSourceDto Source,
    SupplierMappingSourceDto[] MappingSources, SupplierPolicyDto Policy, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierObservationDto(LongId Id, SupplierDetailsDto Observed, string SourceReference,
    string TrustedChannelReference, DateTimeOffset CheckedAt, string Reason);
public sealed record SupplierDecisionDto(LongId Id, string Decision, string Reason, DateTimeOffset DecidedAt, LongId? VersionId, string? Version);
public sealed record SupplierProposalDto(LongId Id, LongId SupplierId, string Version, SupplierChangeInputDto Input,
    SupplierSourceDto Source, SupplierMappingSourceDto[] MappingSources, SupplierPolicyDto Policy, string Reason,
    DateTimeOffset ProposedAt, string State, SupplierObservationDto? Observation, SupplierDecisionDto? Decision);
public sealed record SupplierProposalListDto(SupplierOwnerDto Owner, SupplierProposalDto[] Items, LongId? NextProposalId);
public sealed record SupplierCurrentDto(LongId SupplierId, LongId? VersionId, string Version, SupplierProposalDto? Proposal);
public sealed record SupplierDecisionPreviewDto(SupplierProposalDto Proposal, string Decision, LongId? ObservationId, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierResultDto(LongId SupplierId, LongId ProposalId, string ProposalVersion, LongId RecordId,
    string Operation, LongId? VersionId, string? Version);
public sealed record SupplierRecoveryDto(string Status, SupplierResultDto? Result);
public sealed record SupplierChoiceMappingDto(SupplierMappingInputDto Mapping, ProcurementPackageDto Package);
public sealed record SupplierChoiceDto(LongId SupplierId, LongId VersionId, string Version, string SubjectReference,
    string DisplayName, SupplierChoiceMappingDto[] Mappings);
public sealed record SupplierChoicesDto(ProcurementLocationDto Location, SupplierChoiceDto[] Items, LongId? NextSupplierId);

public static class SupplierEndpoints
{
    public static void MapSupplierEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/suppliers").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (SupplierReadDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), Access(request.Access), ct);
            return TypedResults.Ok(new SupplierTargetsDto([.. row["items"].AsBsonArray.Select(Target)]));
        }).WithName("SupplierTargets");
        group.MapPost("/locations/query", async (SupplierOwnerQueryDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.LocationsAsync(Actor(http), Access(request.Access), Owner(request.Owner), ct);
            return TypedResults.Ok(new SupplierLocationsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("SupplierLocations");
        group.MapPost("/policies/query", async (SupplierOwnerQueryDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PoliciesAsync(Actor(http), Access(request.Access), Owner(request.Owner), ct);
            return TypedResults.Ok(new SupplierPoliciesDto([.. row["items"].AsBsonArray.Select(Policy)]));
        }).WithName("SupplierVerificationPolicies");
        group.MapPost("/packages/query", async (SupplierPackageQueryDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PackagesAsync(Actor(http), Access(request.Access), Location(request.Location), request.Offset, request.Limit, request.Search, ct);
            return TypedResults.Ok(new ProcurementPackagesDto([.. row["items"].AsBsonArray.Select(R.Package)], row["offset"].AsInt32, row["hasMore"].AsBoolean));
        }).WithName("SupplierCatalogPackages");
        group.MapPost("/preview/query", async (SupplierPreviewInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw SupplierRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new SupplierPreviewDto(Input(row["input"]), Source(row["source"]), MappingSources(row["mappingSources"]),
                Policy(row["policy"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("SupplierProposalPreview");
        group.MapPost("/proposals", async (SupplierProposeDto request, HttpContext http, [FromServices] SupplierService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw SupplierRules.Invalid();
            return TypedResults.Ok(Result(await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeSupplier");
        group.MapPost("/proposals/query", async (SupplierListInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Access(request.Access), Owner(request.Owner), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierProposalListDto(Owner(row["owner"]), [.. row["items"].AsBsonArray.Select(Proposal)], Id(row["nextProposalId"])));
        }).WithName("SupplierProposalHistory");
        group.MapPost("/proposals/{id}/query", async (string id, SupplierReadDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Proposal(await service.ReadAsync(Actor(http), Access(request.Access), LongId.Parse(id).Value, ct)));
        }).WithName("SupplierProposalRead");
        group.MapPost("/current/query", async (SupplierCurrentInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.CurrentAsync(Actor(http), Access(request.Access), request.SupplierId.Value, ct);
            return TypedResults.Ok(new SupplierCurrentDto(new(row["supplierId"].AsInt64), Id(row["versionId"]), Number(row["version"]),
                row["proposal"].IsBsonNull ? null : Proposal(row["proposal"])));
        }).WithName("SupplierCurrentVersion");
        group.MapPost("/proposals/{id}/observations", async (string id, SupplierObserveDto request, HttpContext http, [FromServices] SupplierService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            if (request.Observed is null) throw SupplierRules.Invalid();
            return TypedResults.Ok(Result(await service.ObserveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.Observed.Domain(), request.PolicyId.Value, SupplierChangeInputDto.Version(request.PolicyRevision), request.TrustedChannelReference,
                request.SourceReference, request.Attested, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ObserveSupplier");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, SupplierDecisionPreviewInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), request.ObservationId?.Value, ct);
            return TypedResults.Ok(new SupplierDecisionPreviewDto(Proposal(row["proposal"]), row["decision"].AsString, Id(row["observationId"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("SupplierDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, SupplierDecisionInputDto request, HttpContext http, [FromServices] SupplierService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.ObservationId?.Value, request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideSupplier");
        group.MapPost("/operations/query", async (SupplierRecoveryInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw SupplierRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new SupplierRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverSupplierOperation");
        group.MapPost("/choices/query", async (SupplierChoicesInputDto request, HttpContext http, [FromServices] SupplierService service, CancellationToken ct) => {
            if (request.Purpose != "supplier-selection") throw SupplierRules.Invalid();
            var row = await service.ChoicesAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierChoicesDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Choice)], Id(row["nextSupplierId"])));
        }).WithName("ApprovedSupplierChoices");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "supplier-review") throw SupplierRules.Invalid(); }
    private static SupplierAccess Access(string value) => value switch { "propose" => SupplierAccess.Propose, "approve" => SupplierAccess.Approve, _ => throw SupplierRules.Invalid() };
    private static SupplierDecision Decision(string value) => value switch { "approve" => SupplierDecision.Approve, "reject" => SupplierDecision.Reject, _ => throw SupplierRules.Invalid() };
    private static SupplierOwner Owner(SupplierOwnerDto value) => value is null ? throw SupplierRules.Invalid() : value.Domain();
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw SupplierRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static string? OptionalNumber(BsonValue value) => value.IsBsonNull ? null : Number(value);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static SupplierOwnerDto Owner(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64), new(value["channelId"].AsInt64));
    private static SupplierSourceDto Source(BsonValue value) => new(R.Label(value["organization"]), R.Label(value["shop"]), R.Label(value["channel"]));
    private static SupplierTargetDto Target(BsonValue value) => new(Owner(value["owner"]), R.Label(value["organization"]), R.Label(value["shop"]), R.Label(value["channel"]));
    private static SupplierDetailsDto Details(BsonValue value) => new(value["subjectReference"].AsString, value["legalName"].AsString, value["displayName"].AsString,
        Text(value["registrationNamespace"]), Text(value["registrationValue"]), value["address"].AsString,
        value["contactName"].AsString, value["contactMethod"].AsString, value["contactValue"].AsString);
    private static SupplierMappingInputDto Mapping(BsonValue value) => new(R.Location(value["location"]), value["sourceSystem"].AsString,
        value["sourceAccountReference"].AsString, value["externalItemCode"].AsString, new(value["skuId"].AsInt64), Number(value["expectedSkuRevision"]),
        new(value["definitionId"].AsInt64), Number(value["definitionVersion"]), Date(value["validFrom"]), Date(value["validUntil"]));
    private static SupplierChangeInputDto Input(BsonValue value) => new(Owner(value["owner"]), Id(value["supplierId"]), Number(value["baseVersion"]),
        new(value["policyId"].AsInt64), Number(value["policyRevision"]), Details(value["details"]), [.. value["mappings"].AsBsonArray.Select(Mapping)]);
    private static SupplierPolicyDto Policy(BsonValue value) => new(new(value["id"].AsInt64), Number(value["revision"]), Owner(value["owner"]),
        value["subjectReference"].AsString, value["legalName"].AsString, Text(value["registrationNamespace"]), Text(value["registrationValue"]),
        value["method"].AsString, value["trustedChannelReference"].AsString, value["operatingOwnerReference"].AsString, value["approvalReference"].AsString,
        Date(value["approvedAt"]), Date(value["expiresAt"]), Number(value["maxObservationAgeSeconds"]), value["allowReferenceOnly"].AsBoolean, value["active"].AsBoolean);
    private static SupplierMappingSourceDto[] MappingSources(BsonValue value) => [.. value.AsBsonArray.Select(row => new SupplierMappingSourceDto(R.Location(row["location"]),
        R.Label(row["organization"]), R.Label(row["shop"]), R.Label(row["warehouse"]), R.Label(row["channel"]), R.Package(row["package"])))];
    private static SupplierProposalDto Proposal(BsonValue value) {
        var observation = value["observation"]; var decision = value["decision"];
        return new(new(value["id"].AsInt64), new(value["supplierId"].AsInt64), Number(value["version"]), Input(value["input"]), Source(value["source"]),
            MappingSources(value["mappingSources"]), Policy(value["policy"]), value["reason"].AsString, Date(value["proposedAt"]), value["state"].AsString,
            observation.IsBsonNull ? null : new(new(observation["id"].AsInt64), Details(observation["observed"]), observation["sourceReference"].AsString,
                observation["trustedChannelReference"].AsString, Date(observation["checkedAt"]), observation["reason"].AsString),
            decision.IsBsonNull ? null : new(new(decision["id"].AsInt64), decision["decision"].AsString, decision["reason"].AsString,
                Date(decision["decidedAt"]), Id(decision["versionId"]), OptionalNumber(decision["version"])));
    }
    private static SupplierResultDto Result(BsonValue value) => new(new(value["supplierId"].AsInt64), new(value["proposalId"].AsInt64), Number(value["proposalVersion"]),
        new(value["recordId"].AsInt64), value["operation"].AsString, Id(value["versionId"]), OptionalNumber(value["version"]));
    private static SupplierChoiceDto Choice(BsonValue value) => new(new(value["supplierId"].AsInt64), new(value["versionId"].AsInt64), Number(value["version"]),
        value["subjectReference"].AsString, value["displayName"].AsString,
        [.. value["mappings"].AsBsonArray.Select(row => new SupplierChoiceMappingDto(Mapping(row["mapping"]), R.Package(row["package"])))]);
}

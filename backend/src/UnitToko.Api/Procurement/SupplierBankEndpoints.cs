using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankInputDto([property: JsonRequired] SupplierOwnerDto Owner,
    [property: JsonRequired] LongId SupplierId, [property: JsonRequired] LongId SupplierVersionId,
    [property: JsonRequired] string SupplierVersion, [property: JsonRequired] string BaseVersion,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] ReceivingAccountDetailsDto Details) {
    internal SupplierBankInput Domain() {
        if (Owner is null || Details is null) throw SupplierBankRules.Invalid();
        return SupplierBankRules.Copy(new(Owner.Domain(), SupplierId.Value, SupplierVersionId.Value,
            SupplierChangeInputDto.Version(SupplierVersion), SupplierChangeInputDto.Version(BaseVersion, true),
            PolicyId.Value, SupplierChangeInputDto.Version(PolicyRevision), Details.Domain()));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankListInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] SupplierOwnerDto Owner, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankPolicyInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] SupplierOwnerDto Owner, [property: JsonRequired] LongId SupplierId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankCurrentInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId SupplierId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] SupplierBankInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankProposeDto([property: JsonRequired] SupplierBankInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankObservationPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ReceivingAccountDetailsDto Observed, [property: JsonRequired] string SourceReference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankObserveDto([property: JsonRequired] ReceivingAccountDetailsDto Observed,
    [property: JsonRequired] string SourceReference, [property: JsonRequired] string Binding,
    [property: JsonRequired] bool Attested, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankDecisionPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision, [property: JsonRequired] LongId? ObservationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] LongId? ObservationId, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierBankRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record SupplierBankSupplierDto(LongId Id, LongId VersionId, string Version, string SubjectReference, string LegalName, string DisplayName);
public sealed record SupplierBankMaskedDetailsDto(string BankCode, string MaskedAccount, string Currency);
public sealed record SupplierBankChoiceDto(SupplierBankSupplierDto Supplier, LongId? BeneficiaryId, string BankVersion);
public sealed record SupplierBankChoicesDto(SupplierOwnerDto Owner, SupplierSourceDto Source, SupplierBankChoiceDto[] Items, LongId? NextSupplierId);
public sealed record SupplierBankPolicyDto(LongId Id, SupplierOwnerDto Owner, LongId SupplierId, LongId SupplierVersionId,
    string SupplierVersion, string SubjectReference, string Revision, string BankCode, string BankName, string Currency,
    string Method, string TrustedChannelReference, string AuthorizationReference, string OperatingOwnerReference,
    string ApprovalReference, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, string MaxObservationAgeSeconds,
    bool AllowReferenceOnly, bool Active);
public sealed record SupplierBankPoliciesDto(SupplierOwnerDto Owner, LongId SupplierId, SupplierBankPolicyDto[] Items);
public sealed record SupplierBankPreviewDto(SupplierBankInputDto Input, SupplierSourceDto Source, SupplierBankSupplierDto Supplier,
    string Binding, DateTimeOffset ObservedAt, SupplierBankPolicyDto Policy);
public sealed record SupplierBankSummaryDto(LongId Id, LongId BeneficiaryId, SupplierBankSupplierDto Supplier,
    string BaseVersion, string BankVersion, SupplierBankMaskedDetailsDto Details, string State, DateTimeOffset ProposedAt);
public sealed record SupplierBankHistoryDto(SupplierOwnerDto Owner, SupplierBankSummaryDto[] Items, LongId? NextProposalId);
public sealed record SupplierBankCurrentDto(LongId SupplierId, LongId? BeneficiaryId, LongId? VersionId,
    string Version, LongId? ProposalId, SupplierBankMaskedDetailsDto? Details, bool Eligible);
public sealed record SupplierBankObservationDto(LongId Id, ReceivingAccountDetailsDto Observed,
    string SourceReference, string Reason, DateTimeOffset CheckedAt);
public sealed record SupplierBankDecisionDto(LongId Id, string Decision, string Reason, DateTimeOffset DecidedAt, LongId? VersionId, string? Version);
public sealed record SupplierBankProposalDto(LongId Id, LongId BeneficiaryId, string Version, SupplierBankInputDto Input,
    SupplierSourceDto Source, SupplierBankSupplierDto Supplier, SupplierBankPolicyDto Policy, string Reason,
    DateTimeOffset ProposedAt, string State, SupplierBankObservationDto? Observation, SupplierBankDecisionDto? Decision);
public sealed record SupplierBankObservationPreviewDto(SupplierBankProposalDto Proposal, ReceivingAccountDetailsDto Observed,
    string SourceReference, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierBankDecisionPreviewDto(SupplierBankProposalDto Proposal, string Decision,
    LongId? ObservationId, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierBankResultDto(LongId BeneficiaryId, LongId ProposalId, string ProposalVersion,
    LongId RecordId, string Operation, LongId? VersionId, string? Version);
public sealed record SupplierBankRecoveryDto(string Status, SupplierBankResultDto? Result);

public static class SupplierBankEndpoints
{
    public static void MapSupplierBankEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/suppliers/bank-beneficiaries").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (SupplierBankReadDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new SupplierTargetsDto([.. row["items"].AsBsonArray.Select(Target)]));
        }).WithName("SupplierBankTargets");
        group.MapPost("/suppliers/query", async (SupplierBankListInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.SuppliersAsync(Actor(http), Owner(request.Owner), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierBankChoicesDto(Owner(row["owner"]), Source(row["source"]),
                [.. row["items"].AsBsonArray.Select(item => new SupplierBankChoiceDto(Supplier(item["supplier"]), Id(item["beneficiaryId"]), Number(item["bankVersion"])))], Id(row["nextSupplierId"])));
        }).WithName("SupplierBankSuppliers");
        group.MapPost("/policies/query", async (SupplierBankPolicyInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PoliciesAsync(Actor(http), Owner(request.Owner), request.SupplierId.Value, ct);
            return TypedResults.Ok(new SupplierBankPoliciesDto(Owner(row["owner"]), new(row["supplierId"].AsInt64), [.. row["items"].AsBsonArray.Select(Policy)]));
        }).WithName("SupplierBankPolicies");
        group.MapPost("/preview/query", async (SupplierBankPreviewInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw SupplierBankRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new SupplierBankPreviewDto(Input(row["input"]), Source(row["source"]), Supplier(row["supplier"]),
                row["binding"].AsString, Date(row["observedAt"]), Policy(row["policy"])));
        }).WithName("SupplierBankPreview");
        group.MapPost("/proposals", async (SupplierBankProposeDto request, HttpContext http, [FromServices] SupplierBankService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw SupplierBankRules.Invalid();
            return TypedResults.Ok(Result(await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeSupplierBank");
        group.MapPost("/proposals/query", async (SupplierBankListInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Owner(request.Owner), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierBankHistoryDto(Owner(row["owner"]), [.. row["items"].AsBsonArray.Select(Summary)], Id(row["nextProposalId"])));
        }).WithName("SupplierBankHistory");
        group.MapPost("/proposals/{id}/query", async (string id, SupplierBankReadDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Proposal(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("SupplierBankRead");
        group.MapPost("/current/query", async (SupplierBankCurrentInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.CurrentAsync(Actor(http), request.SupplierId.Value, ct);
            return TypedResults.Ok(new SupplierBankCurrentDto(new(row["supplierId"].AsInt64), Id(row["beneficiaryId"]), Id(row["versionId"]),
                Number(row["version"]), Id(row["proposalId"]), row["details"].IsBsonNull ? null : Masked(row["details"]), row["eligible"].AsBoolean));
        }).WithName("SupplierBankCurrent");
        group.MapPost("/proposals/{id}/observation-preview/query", async (string id, SupplierBankObservationPreviewInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Observed is null) throw SupplierBankRules.Invalid();
            var row = await service.PreviewObservationAsync(Actor(http), LongId.Parse(id).Value, request.Observed.Domain(), request.SourceReference, ct);
            return TypedResults.Ok(new SupplierBankObservationPreviewDto(Proposal(row["proposal"]), ReceivingAccountDetailsDto.From(row["observed"]),
                row["sourceReference"].AsString, row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("SupplierBankObservationPreview");
        group.MapPost("/proposals/{id}/observations", async (string id, SupplierBankObserveDto request, HttpContext http, [FromServices] SupplierBankService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            if (request.Observed is null) throw SupplierBankRules.Invalid();
            return TypedResults.Ok(Result(await service.ObserveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.Observed.Domain(), request.SourceReference, request.Binding, request.Attested,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ObserveSupplierBank");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, SupplierBankDecisionPreviewInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value,
                Decision(request.Decision), request.ObservationId?.Value, ct);
            return TypedResults.Ok(new SupplierBankDecisionPreviewDto(Proposal(row["proposal"]), row["decision"].AsString,
                Id(row["observationId"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("SupplierBankDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, SupplierBankDecideDto request, HttpContext http, [FromServices] SupplierBankService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.ObservationId?.Value, request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideSupplierBank");
        group.MapPost("/operations/query", async (SupplierBankRecoveryInputDto request, HttpContext http, [FromServices] SupplierBankService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw SupplierBankRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new SupplierBankRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverSupplierBank");
    }
    private static ActorContext Actor(HttpContext http) { PaymentAllocationEndpoints.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "supplier-bank-review") throw SupplierBankRules.Invalid(); }
    private static SupplierBankDecision Decision(string value) => value switch { "approve" => SupplierBankDecision.Approve, "reject" => SupplierBankDecision.Reject, _ => throw SupplierBankRules.Invalid() };
    private static SupplierOwner Owner(SupplierOwnerDto value) => value is null ? throw SupplierBankRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? OptionalNumber(BsonValue value) => value.IsBsonNull ? null : Number(value);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static SupplierOwnerDto Owner(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64), new(value["channelId"].AsInt64));
    private static SupplierSourceDto Source(BsonValue value) => new(R.Label(value["organization"]), R.Label(value["shop"]), R.Label(value["channel"]));
    private static SupplierTargetDto Target(BsonValue value) => new(Owner(value["owner"]), R.Label(value["organization"]), R.Label(value["shop"]), R.Label(value["channel"]));
    private static SupplierBankSupplierDto Supplier(BsonValue value) => new(new(value["id"].AsInt64), new(value["versionId"].AsInt64), Number(value["version"]),
        value["subjectReference"].AsString, value["legalName"].AsString, value["displayName"].AsString);
    private static SupplierBankMaskedDetailsDto Masked(BsonValue value) => new(value["bankCode"].AsString, value["maskedAccount"].AsString, value["currency"].AsString);
    private static SupplierBankInputDto Input(BsonValue value) => new(Owner(value["owner"]), new(value["supplierId"].AsInt64), new(value["supplierVersionId"].AsInt64),
        Number(value["supplierVersion"]), Number(value["baseVersion"]), new(value["policyId"].AsInt64), Number(value["policyRevision"]), ReceivingAccountDetailsDto.From(value["details"]));
    private static SupplierBankPolicyDto Policy(BsonValue value) => new(new(value["id"].AsInt64), Owner(value["owner"]), new(value["supplierId"].AsInt64),
        new(value["supplierVersionId"].AsInt64), Number(value["supplierVersion"]), value["subjectReference"].AsString, Number(value["revision"]),
        value["bankCode"].AsString, value["bankName"].AsString, value["currency"].AsString, value["method"].AsString, value["trustedChannelReference"].AsString,
        value["authorizationReference"].AsString, value["operatingOwnerReference"].AsString, value["approvalReference"].AsString,
        Date(value["approvedAt"]), Date(value["expiresAt"]), Number(value["maxObservationAgeSeconds"]), value["allowReferenceOnly"].AsBoolean, value["active"].AsBoolean);
    private static SupplierBankSummaryDto Summary(BsonValue value) => new(new(value["id"].AsInt64), new(value["beneficiaryId"].AsInt64), Supplier(value["supplier"]),
        Number(value["baseVersion"]), Number(value["bankVersion"]), Masked(value["details"]), value["state"].AsString, Date(value["proposedAt"]));
    private static SupplierBankProposalDto Proposal(BsonValue value) => new(new(value["id"].AsInt64), new(value["beneficiaryId"].AsInt64), Number(value["version"]),
        Input(value["input"]), Source(value["source"]), Supplier(value["supplier"]), Policy(value["policy"]), value["reason"].AsString,
        Date(value["proposedAt"]), value["state"].AsString, value["observation"].IsBsonNull ? null : Observation(value["observation"]), value["decision"].IsBsonNull ? null : DecisionView(value["decision"]));
    private static SupplierBankObservationDto Observation(BsonValue value) => new(new(value["id"].AsInt64), ReceivingAccountDetailsDto.From(value["observed"]),
        value["sourceReference"].AsString, value["reason"].AsString, Date(value["checkedAt"]));
    private static SupplierBankDecisionDto DecisionView(BsonValue value) => new(new(value["id"].AsInt64), value["decision"].AsString,
        value["reason"].AsString, Date(value["decidedAt"]), Id(value["versionId"]), OptionalNumber(value["version"]));
    private static SupplierBankResultDto Result(BsonValue value) => new(new(value["beneficiaryId"].AsInt64), new(value["proposalId"].AsInt64), Number(value["proposalVersion"]),
        new(value["recordId"].AsInt64), value["operation"].AsString, Id(value["versionId"]), OptionalNumber(value["version"]));
}

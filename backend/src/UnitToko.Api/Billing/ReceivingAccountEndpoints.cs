using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountDetailsDto([property: JsonRequired] string BankCode, [property: JsonRequired] string AccountNumber, [property: JsonRequired] string HolderName, [property: JsonRequired] string Currency) {
    internal ReceivingAccountDetails Domain() => ReceivingAccountPolicy.Copy(new(BankCode, AccountNumber, HolderName, Currency));
    internal static ReceivingAccountDetailsDto From(BsonValue row) => new(row["bankCode"].AsString, row["accountNumber"].AsString, row["holderName"].AsString, row["currency"].AsString);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId? AfterId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountProposeDto([property: JsonRequired] LongId? AccountId, [property: JsonRequired] LongId PolicyId,
    [property: JsonRequired] string PolicyRevision, [property: JsonRequired] ReceivingAccountDetailsDto Details, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountObserveDto([property: JsonRequired] ReceivingAccountDetailsDto Details, [property: JsonRequired] string SourceReference,
    [property: JsonRequired] bool Attested, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountDecisionDto([property: JsonRequired] string Decision, [property: JsonRequired] LongId? ObservationId, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceivingAccountRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ReceivingScopeDto(LongId FulfillmentOrganizationId, LongId ShopId, LongId ChannelId, LongId? WarehouseId);
public sealed record ReceivingPolicyDto(LongId Id, string Revision, ReceivingScopeDto Scope, string BankCode, string BankName, string Method,
    string TrustedChannelReference, string OperatingOwnerReference, string ApprovalReference, bool Active, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, string MaxObservationAgeSeconds);
public sealed record ReceivingSummaryDto(LongId ProposalId, LongId AccountId, ReceivingScopeDto Scope, string BankCode, string MaskedAccount, string BaseVersion, string CurrentVersion, LongId ActorId, DateTimeOffset RecordedAt, string State);
public sealed record ReceivingPageDto(ReceivingSummaryDto[] Items, bool HasMore);
public sealed record ReceivingPolicyPageDto(ReceivingPolicyDto[] Items, bool HasMore);
public sealed record ReceivingProposalDto(LongId Id, LongId AccountId, string BaseVersion, LongId ActorId, DateTimeOffset RecordedAt, string Reason, ReceivingAccountDetailsDto Details, ReceivingPolicyDto Policy);
public sealed record ReceivingObservationDto(LongId Id, LongId ActorId, DateTimeOffset CheckedAt, string SourceReference, string Reason, ReceivingAccountDetailsDto Details);
public sealed record ReceivingDecisionDto(LongId Id, LongId ActorId, DateTimeOffset RecordedAt, string Decision, string Reason, LongId? ObservationId, LongId? VersionId, string? AccountVersion);
public sealed record ReceivingVersionDto(LongId Id, string Version, ReceivingAccountDetailsDto Details, DateTimeOffset EffectiveAt, LongId? PreviousVersionId);
public sealed record ReceivingViewDto(ReceivingProposalDto Proposal, ReceivingObservationDto? Observation, ReceivingDecisionDto? Decision, ReceivingVersionDto? CurrentVersion);
public sealed record ReceivingResultDto(LongId AccountId, LongId ProposalId, LongId RecordId, string Operation, string? AccountVersion, DateTimeOffset RecordedAt);
public sealed record ReceivingRecoveryResultDto(string Status, ReceivingResultDto? Result);

public static class ReceivingAccountEndpoints
{
    private static string Number(BsonValue value) => OrderDraftInputDto.Number(value.AsInt64);
    private static LongId? Optional(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime());
    private static ReceivingScopeDto Scope(BsonValue row) => new(new(row["fulfillmentOrganizationId"].AsInt64), new(row["shopId"].AsInt64), new(row["channelId"].AsInt64), Optional(row["warehouseId"]));
    private static ReceivingPolicyDto Policy(BsonValue row) => new(new(row["_id"].AsInt64), Number(row["revision"]), Scope(row["scope"]), row["bankCode"].AsString, row["bankName"].AsString,
        row["method"].AsString, row["trustedChannelReference"].AsString, row["operatingOwnerReference"].AsString, row["approvalReference"].AsString, row["active"].AsBoolean, Date(row["approvedAt"]), Date(row["expiresAt"]), Number(row["maxObservationAgeSeconds"]));
    private static ReceivingResultDto Result(BsonDocument row) => new(new(row["accountId"].AsInt64), new(row["proposalId"].AsInt64), new(row["recordId"].AsInt64), row["operation"].AsString,
        row["accountVersion"].IsBsonNull ? null : Number(row["accountVersion"]), Date(row["recordedAt"]));
    private static ReceivingViewDto View(BsonDocument view) {
        var p = view["proposal"]; var o = view["observation"]; var d = view["decision"]; var v = view["currentVersion"];
        return new(new(new(p["_id"].AsInt64), new(p["accountId"].AsInt64), Number(p["baseVersion"]), new(p["actorId"].AsInt64), Date(p["recordedAt"]), p["reason"].AsString, ReceivingAccountDetailsDto.From(p["details"]), Policy(p["policy"])),
            o.IsBsonNull ? null : new(new(o["_id"].AsInt64), new(o["actorId"].AsInt64), Date(o["checkedAt"]), o["sourceReference"].AsString, o["reason"].AsString, ReceivingAccountDetailsDto.From(o["details"])),
            d.IsBsonNull ? null : new(new(d["_id"].AsInt64), new(d["actorId"].AsInt64), Date(d["recordedAt"]), d["decision"].AsString, d["reason"].AsString, Optional(d["observationId"]), Optional(d["versionId"]), d["accountVersion"].IsBsonNull ? null : Number(d["accountVersion"])),
            v.IsBsonNull ? null : new(new(v["_id"].AsInt64), Number(v["version"]), ReceivingAccountDetailsDto.From(v["details"]), Date(v["effectiveAt"]), Optional(v["previousVersionId"])));
    }
    public static void MapReceivingAccountEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/receiving-accounts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/policies/query", async (ReceivingAccountQueryDto request, HttpContext http, ReceivingAccountService service, CancellationToken ct) => {
            Read(request.Purpose, http); var rows = await service.ListPoliciesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.AfterId?.Value, ct);
            return TypedResults.Ok(new ReceivingPolicyPageDto(rows.Take(25).Select(Policy).ToArray(), rows.Count > 25));
        }).WithName("ListReceivingAccountPolicies");
        group.MapPost("/proposals/query", async (ReceivingAccountQueryDto request, HttpContext http, ReceivingAccountService service, CancellationToken ct) => {
            Read(request.Purpose, http); var rows = await service.ListProposalsAsync(http.Features.Get<ResolvedActor>()!.Actor, request.AfterId?.Value, ct);
            return TypedResults.Ok(new ReceivingPageDto(rows.Take(25).Select(row => new ReceivingSummaryDto(new(row["proposalId"].AsInt64), new(row["accountId"].AsInt64), Scope(row["scope"]), row["bankCode"].AsString,
                row["maskedAccount"].AsString, Number(row["baseVersion"]), Number(row["currentVersion"]), new(row["actorId"].AsInt64), Date(row["recordedAt"]), row["state"].AsString)).ToArray(), rows.Count > 25));
        }).WithName("ListReceivingAccountProposals");
        group.MapPost("/proposals/{id}/query", async (string id, ReceivingAccountReadDto request, HttpContext http, ReceivingAccountService service, CancellationToken ct) => {
            Read(request.Purpose, http); http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(View(await service.ReadProposalAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadReceivingAccountProposal");
        group.MapPost("/proposals", async (ReceivingAccountProposeDto request, HttpContext http, ReceivingAccountService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (request.Details is null) throw ReceivingAccountPolicy.Invalid();
            return TypedResults.Ok(Result(await service.ProposeAsync(http.Features.Get<ResolvedActor>()!.Actor, request.AccountId?.Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.PolicyId.Value, LongId.Parse(request.PolicyRevision).Value,
                request.Details.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeReceivingAccount");
        group.MapPost("/proposals/{id}/observations", async (string id, ReceivingAccountObserveDto request, HttpContext http, ReceivingAccountService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (request.Details is null) throw ReceivingAccountPolicy.Invalid();
            return TypedResults.Ok(Result(await service.ObserveAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Details.Domain(), request.SourceReference, request.Attested,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ObserveReceivingAccount");
        group.MapPost("/proposals/{id}/decisions", async (string id, ReceivingAccountDecisionDto request, HttpContext http, ReceivingAccountService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); var decision = request.Decision switch { "approve" => ReceivingAccountDecision.Approve, "reject" => ReceivingAccountDecision.Reject, "withdraw" => ReceivingAccountDecision.Withdraw, _ => throw ReceivingAccountPolicy.Invalid() };
            return TypedResults.Ok(Result(await service.DecideAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), decision, request.ObservationId?.Value,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideReceivingAccount");
        group.MapPost("/operations/query", async (ReceivingAccountRecoveryDto request, HttpContext http, ReceivingAccountService service, CancellationToken ct) => {
            Private(http); if (request.Purpose != "operation-recovery") throw ReceivingAccountPolicy.Invalid();
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Operation, request.Key, ct);
            return TypedResults.Ok(new ReceivingRecoveryResultDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverReceivingAccountOperation");
    }
    private static void Read(string purpose, HttpContext http) { if (purpose != "receiving-account-review") throw ReceivingAccountPolicy.Invalid(); Private(http); }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}

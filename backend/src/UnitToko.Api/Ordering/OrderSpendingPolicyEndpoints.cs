using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Ordering;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyTargetDto([property: JsonRequired] ScopeNodeKind Kind, [property: JsonRequired] LongId Id);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingRuleDto([property: JsonRequired] string Mode, [property: JsonRequired] Money? Threshold) {
    internal OrderSpendingRule Domain() => new(Mode switch { "every_order" => OrderSpendingMode.EveryOrder,
        "above_threshold" => OrderSpendingMode.AboveThreshold, "not_required" => OrderSpendingMode.NotRequired, _ => throw OrderSpendingPolicyRules.Invalid() }, Threshold);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyInputDto([property: JsonRequired] OrderSpendingPolicyTargetDto Target,
    [property: JsonRequired] string ExpectedScopeVersion, [property: JsonRequired] string ExpectedProfileVersion,
    [property: JsonRequired] string ExpectedPolicyVersion, [property: JsonRequired] OrderSpendingRuleDto Rule,
    [property: JsonRequired] DateTimeOffset ValidFrom, [property: JsonRequired] DateTimeOffset ValidUntil) {
    internal OrderSpendingPolicyInput Domain() {
        if (Target is null || Rule is null) throw OrderSpendingPolicyRules.Invalid();
        return OrderSpendingPolicyRules.Copy(new(new(Target.Kind, Target.Id.Value), OrderSpendingPolicyEndpoints.Version(ExpectedScopeVersion),
            OrderSpendingPolicyEndpoints.Version(ExpectedProfileVersion), OrderSpendingPolicyEndpoints.Version(ExpectedPolicyVersion), Rule.Domain(), ValidFrom, ValidUntil));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyTargetsRequestDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderSpendingPolicyTargetDto Target);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderSpendingPolicyInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyProposeDto([property: JsonRequired] OrderSpendingPolicyInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyHistoryRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderSpendingPolicyTargetDto Target, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyDecisionPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSpendingPolicyRecoveryRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record OrderSpendingPolicyTargetChoiceDto(OrderSpendingPolicyTargetDto Target, OrderContextDto Context, string Code, string Name,
    string ExpectedScopeVersion, string ExpectedProfileVersion, string PolicyVersion, LongId? PolicyId, LongId? VersionId);
public sealed record OrderSpendingPolicyTargetsDto(OrderSpendingPolicyTargetChoiceDto[] Items, bool HasMore, LongId? NextTargetId);
public sealed record OrderSpendingPolicyPreviewViewDto(OrderSpendingPolicyTargetDto Target, OrderContextDto Context, string Code, string Name,
    string ScopeVersion, string ProfileVersion, string PolicyVersion, OrderSpendingRuleDto Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string SourceHash);
public sealed record OrderSpendingPolicyPreviewDto(string Binding, OrderSpendingPolicyPreviewViewDto View);
public sealed record OrderSpendingPolicyCurrentDto(OrderSpendingPolicyTargetDto Target, OrderContextDto Context, string Code, string Name, LongId? PolicyId,
    string Version, LongId? VersionId, OrderSpendingRuleDto? Rule, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, DateTimeOffset? ApprovedAt, bool Current, bool Active);
public sealed record OrderSpendingPolicyProposalDto(LongId ProposalId, LongId PolicyId, string Revision, OrderSpendingPolicyTargetDto Target, OrderContextDto Context,
    string Code, string Name, OrderSpendingRuleDto Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string BaseVersion,
    DateTimeOffset ProposedAt, string State, string? Decision, string? PolicyVersion, LongId? VersionId, bool BindingCurrent);
public sealed record OrderSpendingPolicyHistoryDto(OrderSpendingPolicyProposalDto[] Items, LongId? NextProposalId);
public sealed record OrderSpendingPolicyDecisionPreviewDto(string Binding, OrderSpendingPolicyProposalDto View);
public sealed record OrderSpendingPolicyResultDto(LongId PolicyId, LongId ProposalId, LongId RecordId, string Operation, string? PolicyVersion, LongId? VersionId, DateTimeOffset RecordedAt);
public sealed record OrderSpendingPolicyRecoveryDto(string Status, OrderSpendingPolicyResultDto? Result);

public static class OrderSpendingPolicyEndpoints
{
    public static void MapOrderSpendingPolicyEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/spending-policies").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (OrderSpendingPolicyTargetsRequestDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrderSpendingPolicyTargetsDto([.. row["items"].AsBsonArray.Select(TargetChoice)], row["hasMore"].AsBoolean, Id(row["nextTargetId"])));
        }).WithName("OrderSpendingPolicyTargets");
        group.MapPost("/preview/query", async (OrderSpendingPolicyPreviewRequestDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw OrderSpendingPolicyRules.Invalid();
            var value = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new OrderSpendingPolicyPreviewDto(value.Binding, Preview(value.View)));
        }).WithName("OrderSpendingPolicyPreview");
        group.MapPost("/proposals", async (OrderSpendingPolicyProposeDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw OrderSpendingPolicyRules.Invalid();
            var row = await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(row));
        }).WithName("ProposeOrderSpendingPolicy");
        group.MapPost("/current/query", async (OrderSpendingPolicyQueryDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadAsync(Actor(http), Target(request.Target), ct);
            return TypedResults.Ok(new OrderSpendingPolicyCurrentDto(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
                Id(row["policyId"]), Number(row["version"]), Id(row["versionId"]), row["rule"].IsBsonNull ? null : Rule(row["rule"]),
                OptionalDate(row["validFrom"]), OptionalDate(row["validUntil"]), OptionalDate(row["approvedAt"]), row["current"].AsBoolean, row["active"].AsBoolean));
        }).WithName("CurrentOrderSpendingPolicy");
        group.MapPost("/proposals/query", async (OrderSpendingPolicyHistoryRequestDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListProposalsAsync(Actor(http), Target(request.Target), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrderSpendingPolicyHistoryDto([.. row["items"].AsBsonArray.Select(Proposal)], Id(row["nextProposalId"])));
        }).WithName("OrderSpendingPolicyHistory");
        group.MapPost("/proposals/{id}/query", async (string id, OrderSpendingPolicyReadDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Proposal(await service.ReadProposalAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("OrderSpendingPolicyProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, OrderSpendingPolicyDecisionPreviewRequestDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, 1, Decision(request.Decision), ct);
            return TypedResults.Ok(new OrderSpendingPolicyDecisionPreviewDto(value.Binding, Proposal(value.View)));
        }).WithName("OrderSpendingPolicyDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, OrderSpendingPolicyDecideDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            var row = await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(row));
        }).WithName("DecideOrderSpendingPolicy");
        group.MapPost("/operations/query", async (OrderSpendingPolicyRecoveryRequestDto request, HttpContext http, [FromServices] OrderSpendingPolicyService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw OrderSpendingPolicyRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new OrderSpendingPolicyRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverOrderSpendingPolicy");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string purpose) { if (purpose != "spending-policy-review") throw OrderSpendingPolicyRules.Invalid(); }
    internal static long Version(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0 || version >= long.MaxValue || value != version.ToString(CultureInfo.InvariantCulture)) throw OrderSpendingPolicyRules.Invalid();
        return version;
    }
    private static OrderSpendingPolicyDecision Decision(string value) => value switch { "approve" => OrderSpendingPolicyDecision.Approve, "reject" => OrderSpendingPolicyDecision.Reject, _ => throw OrderSpendingPolicyRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static DateTimeOffset? OptionalDate(BsonValue value) => value.IsBsonNull ? null : Date(value);
    private static OrderSpendingPolicyTarget Target(OrderSpendingPolicyTargetDto value) => value is null ? throw OrderSpendingPolicyRules.Invalid() : new(value.Kind, value.Id.Value);
    private static OrderSpendingPolicyTargetDto Target(BsonValue value) => new(Enum.Parse<ScopeNodeKind>(value["kind"].AsString, true), new(value["id"].AsInt64));
    private static OrderContextDto Context(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["branchId"].AsInt64), Id(value["divisionId"]));
    private static OrderSpendingRuleDto Rule(BsonValue value) => new(value["mode"].AsString, value["threshold"].IsBsonNull ? null : new Money(Decimal128.ToDecimal(value["threshold"].AsDecimal128)));
    private static OrderSpendingPolicyTargetChoiceDto TargetChoice(BsonValue row) => new(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
        Number(row["expectedScopeVersion"]), Number(row["expectedProfileVersion"]), Number(row["policyVersion"]), Id(row["policyId"]), Id(row["versionId"]));
    private static OrderSpendingPolicyPreviewViewDto Preview(BsonValue row) => new(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
        Number(row["scopeVersion"]), Number(row["profileVersion"]), Number(row["policyVersion"]), Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]), row["sourceHash"].AsString);
    private static OrderSpendingPolicyProposalDto Proposal(BsonValue row) => new(new(row["proposalId"].AsInt64), new(row["policyId"].AsInt64), Number(row["revision"]),
        Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString, Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]),
        Number(row["baseVersion"]), Date(row["proposedAt"]), row["state"].AsString, row["decision"].IsBsonNull ? null : row["decision"].AsString,
        row["policyVersion"].IsBsonNull ? null : Number(row["policyVersion"]), Id(row["versionId"]), row["bindingCurrent"].AsBoolean);
    private static OrderSpendingPolicyResultDto Result(BsonValue row) => new(new(row["policyId"].AsInt64), new(row["proposalId"].AsInt64), new(row["recordId"].AsInt64),
        row["operation"].AsString, row["policyVersion"].IsBsonNull ? null : Number(row["policyVersion"]), Id(row["versionId"]), Date(row["recordedAt"]));
}

using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId OrderId, [property: JsonRequired] string OrderVersion, [property: JsonRequired] LongId OrderApprovalId,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision, [property: JsonRequired] Money Amount,
    [property: JsonRequired] string BusinessDate, [property: JsonRequired] string NeededBy) {
    internal ProcurementFundingInput Domain() {
        if (Location is null) throw ProcurementFundingRules.Invalid();
        return ProcurementFundingRules.Copy(new(Location.Domain(), OrderId.Value, Version(OrderVersion), OrderApprovalId.Value,
            PolicyId.Value, Version(PolicyRevision), Amount, Date(BusinessDate), Date(NeededBy)));
    }
    private static DateOnly Date(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw ProcurementFundingRules.Invalid();
    private static long Version(string value) {
        if (value is not { Length: > 0 and <= 19 } || value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0) throw ProcurementFundingRules.Invalid();
        return version;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementFundingInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingSubmitDto([property: JsonRequired] ProcurementFundingInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingDecisionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementFundingRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ProcurementFundingPolicyDto(LongId Id, ProcurementLocationDto Location, string Revision, string[] EligibleRoutes,
    bool AllowInstallments, bool ReserveUndisbursed, bool RequiresAdditionalBudgetSource, bool RequiresAdditionalLimitSource,
    string OperatingOwnerReference, string ProcedureReference, string ApprovalReference, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, bool Active);
public sealed record ProcurementFundingOrderDto(LongId Id, string Version, LongId ApprovalId, LongId ProposalId, string Route, string BusinessDate,
    string Currency, PurchaseOrderSupplierDto Supplier, Money Total, Money RequiredAdvance, PurchaseOrderPaymentTermsDto PaymentTerms,
    PurchaseOrderCommercialReferenceDto CommercialReference, string? ExternalOrderReference, PurchaseOrderLineDto[] Lines);
public sealed record ProcurementFundingCapacityDto(LongId OrderId, string OrderVersion, Money RequiredAdvance, Money Authorized, Money Remaining, string Version);
public sealed record ProcurementFundingOrderChoiceDto(ProcurementFundingOrderDto Order, ProcurementFundingCapacityDto Capacity);
public sealed record ProcurementFundingOrdersDto(ProcurementLocationDto Location, ProcurementSourceDto Source, ProcurementFundingOrderChoiceDto[] Items,
    ProcurementFundingPolicyDto[] Policies, LongId? NextOrderId);
public sealed record ProcurementFundingPreviewDto(ProcurementFundingInputDto Input, ProcurementSourceDto Source, ProcurementFundingOrderDto Order,
    ProcurementFundingPolicyDto Policy, ProcurementFundingCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementFundingDecisionDto(LongId Id, string Decision, string Reason, DateTimeOffset DecidedAt, LongId? AuthorizationId, Money? AuthorizedAmount);
public sealed record ProcurementFundingRequestDto(LongId Id, string Version, ProcurementFundingInputDto Input, ProcurementSourceDto Source,
    ProcurementFundingOrderDto Order, ProcurementFundingPolicyDto Policy, ProcurementFundingCapacityDto Capacity, string Reason,
    DateTimeOffset RequestedAt, string State, ProcurementFundingDecisionDto? Decision);
public sealed record ProcurementFundingHistoryDto(ProcurementLocationDto Location, ProcurementFundingRequestDto[] Items, LongId? NextRequestId);
public sealed record ProcurementFundingDecisionPreviewDto(ProcurementFundingRequestDto Request, string Decision, ProcurementFundingCapacityDto? Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementFundingResultDto(LongId RequestId, string RequestVersion, LongId? DecisionId, LongId? AuthorizationId, Money? AuthorizedAmount, string Status);
public sealed record ProcurementFundingRecoveryDto(string Status, ProcurementFundingResultDto? Result);

public static class ProcurementFundingEndpoints
{
    public static void MapProcurementFundingEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/funding").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementFundingReadDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementFundingTargets");
        group.MapPost("/orders/query", async (ProcurementFundingListInputDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OrdersAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementFundingOrdersDto(R.Location(row["location"]), R.Source(row["source"]),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementFundingOrderChoiceDto(Order(item["order"]), Capacity(item["capacity"])))],
                [.. row["policies"].AsBsonArray.Select(Policy)], Id(row["nextOrderId"])));
        }).WithName("ProcurementFundingOrders");
        group.MapPost("/preview/query", async (ProcurementFundingPreviewInputDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementFundingRules.Invalid();
            return TypedResults.Ok(Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("ProcurementFundingPreview");
        group.MapPost("/requests", async (ProcurementFundingSubmitDto request, HttpContext http, [FromServices] ProcurementFundingService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementFundingRules.Invalid();
            return TypedResults.Ok(Result(await service.RequestAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RequestProcurementFunding");
        group.MapPost("/requests/query", async (ProcurementFundingListInputDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementFundingHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Request)], Id(row["nextRequestId"])));
        }).WithName("ProcurementFundingHistory");
        group.MapPost("/requests/{id}/query", async (string id, ProcurementFundingReadDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Request(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementFundingRead");
        group.MapPost("/requests/{id}/decision-preview/query", async (string id, ProcurementFundingDecisionPreviewInputDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new ProcurementFundingDecisionPreviewDto(Request(row["request"]), row["decision"].AsString,
                row["capacity"].IsBsonNull ? null : Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("ProcurementFundingDecisionPreview");
        group.MapPost("/requests/{id}/decisions", async (string id, ProcurementFundingDecideDto request, HttpContext http, [FromServices] ProcurementFundingService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideProcurementFunding");
        group.MapPost("/operations/query", async (ProcurementFundingRecoveryInputDto request, HttpContext http, [FromServices] ProcurementFundingService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw ProcurementFundingRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementFundingRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementFunding");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "funding-review") throw ProcurementFundingRules.Invalid(); }
    private static ProcurementFundingDecision Decision(string value) => value switch { "approve" => ProcurementFundingDecision.Approve, "reject" => ProcurementFundingDecision.Reject, _ => throw ProcurementFundingRules.Invalid() };
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementFundingRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static Money? OptionalAmount(BsonValue value) => value.IsBsonNull ? null : Amount(value);
    private static ProcurementFundingInputDto Input(BsonValue value) => new(R.Location(value["location"]), new(value["orderId"].AsInt64), Number(value["orderVersion"]),
        new(value["orderApprovalId"].AsInt64), new(value["policyId"].AsInt64), Number(value["policyRevision"]), Amount(value["amount"]), value["businessDate"].AsString, value["neededBy"].AsString);
    private static ProcurementFundingPolicyDto Policy(BsonValue value) => new(new(value["id"].AsInt64), R.Location(value["location"]), Number(value["revision"]),
        [.. value["eligibleRoutes"].AsBsonArray.Select(route => route.AsString)], value["allowInstallments"].AsBoolean, value["reserveUndisbursed"].AsBoolean,
        value["requiresAdditionalBudgetSource"].AsBoolean, value["requiresAdditionalLimitSource"].AsBoolean, value["operatingOwnerReference"].AsString,
        value["procedureReference"].AsString, value["approvalReference"].AsString, Date(value["approvedAt"]), Date(value["expiresAt"]), value["active"].AsBoolean);
    private static ProcurementFundingOrderDto Order(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), new(value["approvalId"].AsInt64), new(value["proposalId"].AsInt64),
        value["route"].AsString, value["businessDate"].AsString, value["currency"].AsString, PurchaseOrderEndpoints.Supplier(value["supplier"]), Amount(value["total"]), Amount(value["requiredAdvance"]),
        new(value["paymentTerms"]["kind"].AsString, value["paymentTerms"]["description"].AsString, Amount(value["paymentTerms"]["requiredAdvance"]),
            value["paymentTerms"]["balanceTrigger"].AsString, Text(value["paymentTerms"]["dueDate"])),
        new(value["commercialReference"]["kind"].AsString, value["commercialReference"]["reference"].AsString, value["commercialReference"]["issuedDate"].AsString, Date(value["commercialReference"]["validUntil"])),
        Text(value["externalOrderReference"]), PurchaseOrderEndpoints.Lines(value["lines"]));
    private static ProcurementFundingCapacityDto Capacity(BsonValue value) => new(new(value["orderId"].AsInt64), Number(value["orderVersion"]), Amount(value["requiredAdvance"]),
        Amount(value["authorized"]), Amount(value["remaining"]), Number(value["version"]));
    private static ProcurementFundingPreviewDto Preview(BsonValue value) => new(Input(value["input"]), R.Source(value["source"]), Order(value["order"]), Policy(value["policy"]),
        Capacity(value["capacity"]), value["binding"].AsString, Date(value["observedAt"]));
    private static ProcurementFundingDecisionDto DecisionView(BsonValue value) => new(new(value["id"].AsInt64), value["decision"].AsString, value["reason"].AsString,
        Date(value["decidedAt"]), Id(value["authorizationId"]), OptionalAmount(value["authorizedAmount"]));
    private static ProcurementFundingRequestDto Request(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), Input(value["input"]), R.Source(value["source"]),
        Order(value["order"]), Policy(value["policy"]), Capacity(value["capacity"]), value["reason"].AsString, Date(value["requestedAt"]), value["state"].AsString,
        value["decision"].IsBsonNull ? null : DecisionView(value["decision"]));
    private static ProcurementFundingResultDto Result(BsonValue value) => new(new(value["requestId"].AsInt64), Number(value["requestVersion"]), Id(value["decisionId"]),
        Id(value["authorizationId"]), OptionalAmount(value["authorizedAmount"]), value["status"].AsString);
}

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
public sealed record PurchaseOrderCommercialReferenceDto([property: JsonRequired] string Kind, [property: JsonRequired] string Reference,
    [property: JsonRequired] string IssuedDate, [property: JsonRequired] DateTimeOffset ValidUntil);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderPaymentTermsDto([property: JsonRequired] string Kind, [property: JsonRequired] string Description,
    [property: JsonRequired] Money RequiredAdvance, [property: JsonRequired] string BalanceTrigger, [property: JsonRequired] string? DueDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderLineInputDto([property: JsonRequired] LongId RequestSkuId, [property: JsonRequired] int Packages,
    [property: JsonRequired] string SourceSystem, [property: JsonRequired] string SourceAccountReference, [property: JsonRequired] string ExternalItemCode,
    [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] string DefinitionVersion,
    [property: JsonRequired] Money NetPricePerPackage, [property: JsonRequired] Money TaxAmount, [property: JsonRequired] Money SupplierChargeAmount);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId RequestId, [property: JsonRequired] string RequestVersion, [property: JsonRequired] LongId RequestDecisionId,
    [property: JsonRequired] LongId SupplierId, [property: JsonRequired] LongId SupplierVersionId, [property: JsonRequired] string SupplierVersion,
    [property: JsonRequired] string BusinessDate, [property: JsonRequired] string Currency,
    [property: JsonRequired] PurchaseOrderCommercialReferenceDto CommercialReference, [property: JsonRequired] string? ExternalOrderReference,
    [property: JsonRequired] PurchaseOrderPaymentTermsDto PaymentTerms, [property: JsonRequired] PurchaseOrderLineInputDto[] Lines) {
    internal PurchaseOrderInput Domain() {
        if (Location is null || CommercialReference is null || PaymentTerms is null || Lines is not { Length: > 0 and <= 100 } || Lines.Any(line => line is null)) throw PurchaseOrderRules.Invalid();
        return PurchaseOrderRules.Copy(new(Location.Domain(), RequestId.Value, Version(RequestVersion), RequestDecisionId.Value,
            SupplierId.Value, SupplierVersionId.Value, Version(SupplierVersion), Date(BusinessDate), Currency,
            new(CommercialReference.Kind, CommercialReference.Reference, Date(CommercialReference.IssuedDate), CommercialReference.ValidUntil), ExternalOrderReference,
            new(PaymentTerms.Kind, PaymentTerms.Description, PaymentTerms.RequiredAdvance, PaymentTerms.BalanceTrigger,
                PaymentTerms.DueDate is null ? null : Date(PaymentTerms.DueDate)),
            [.. Lines.Select(line => new PurchaseOrderLineInput(line.RequestSkuId.Value, line.Packages, line.SourceSystem, line.SourceAccountReference,
                line.ExternalItemCode, line.DefinitionId.Value, Version(line.DefinitionVersion), line.NetPricePerPackage, line.TaxAmount, line.SupplierChargeAmount))]));
    }
    private static DateOnly Date(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw PurchaseOrderRules.Invalid();
    private static long Version(string value) {
        if (value is not { Length: > 0 and <= 19 } || value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0) throw PurchaseOrderRules.Invalid();
        return version;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] PurchaseOrderInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderProposeDto([property: JsonRequired] PurchaseOrderInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderDecisionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseOrderRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record PurchaseOrderRequestLineDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    int Packages, string? Specification, int CommittedPackages, int AvailablePackages);
public sealed record PurchaseOrderRequestDto(LongId Id, string Version, LongId DecisionId, string Route, string Kind, string BusinessDate,
    string? ExternalReference, string Reason, ProcurementSourceDto Source, PurchaseOrderRequestLineDto[] Lines);
public sealed record PurchaseOrderRequestsDto(ProcurementLocationDto Location, PurchaseOrderRequestDto[] Items, LongId? NextRequestId);
public sealed record PurchaseOrderRequestReferenceDto(LongId Id, string Version, LongId DecisionId);
public sealed record PurchaseOrderSupplierDto(LongId Id, LongId VersionId, string Version, string SubjectReference, string DisplayName);
public sealed record PurchaseOrderLineDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    int Packages, string? Specification, string SourceSystem, string SourceAccountReference, string ExternalItemCode,
    Money NetPricePerPackage, Money TaxAmount, Money SupplierChargeAmount, Money NetAmount, Money TotalAmount, string BaseUnits);
public sealed record PurchaseOrderTotalsDto(Money Net, Money Tax, Money SupplierCharges, Money Total);
public sealed record PurchaseOrderCapacityDto(LongId RequestSkuId, int RequestedPackages, int CommittedPackages, int AvailablePackages, string Version);
public sealed record PurchaseOrderPreviewDto(PurchaseOrderInputDto Input, string Route, ProcurementSourceDto Source,
    PurchaseOrderRequestReferenceDto Request, PurchaseOrderSupplierDto Supplier, PurchaseOrderLineDto[] Lines, PurchaseOrderTotalsDto Totals,
    PurchaseOrderCapacityDto[] Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record PurchaseOrderDecisionDto(LongId Id, string Decision, string Reason, DateTimeOffset DecidedAt, LongId? OrderId, string? OrderVersion);
public sealed record PurchaseOrderProposalDto(LongId Id, string Version, PurchaseOrderInputDto Input, string Route, ProcurementSourceDto Source,
    PurchaseOrderRequestReferenceDto Request, PurchaseOrderSupplierDto Supplier, PurchaseOrderLineDto[] Lines, PurchaseOrderTotalsDto Totals,
    PurchaseOrderCapacityDto[] Capacity, string Reason, DateTimeOffset ProposedAt, string State, PurchaseOrderDecisionDto? Decision);
public sealed record PurchaseOrderHistoryDto(ProcurementLocationDto Location, PurchaseOrderProposalDto[] Items, LongId? NextProposalId);
public sealed record PurchaseOrderDecisionPreviewDto(PurchaseOrderProposalDto Proposal, string Decision, PurchaseOrderCapacityDto[] Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record PurchaseOrderResultDto(LongId ProposalId, string ProposalVersion, LongId? DecisionId, LongId? OrderId, string? OrderVersion, string Status);
public sealed record PurchaseOrderRecoveryDto(string Status, PurchaseOrderResultDto? Result);

public static class PurchaseOrderEndpoints
{
    public static void MapPurchaseOrderEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/purchase-orders").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (PurchaseOrderReadDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("PurchaseOrderTargets");
        group.MapPost("/requests/query", async (PurchaseOrderListInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.RequestsAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new PurchaseOrderRequestsDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Request)], Id(row["nextRequestId"])));
        }).WithName("PurchaseOrderRequests");
        group.MapPost("/preview/query", async (PurchaseOrderPreviewInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw PurchaseOrderRules.Invalid();
            return TypedResults.Ok(Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PurchaseOrderPreview");
        group.MapPost("/proposals", async (PurchaseOrderProposeDto request, HttpContext http, [FromServices] PurchaseOrderService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw PurchaseOrderRules.Invalid();
            return TypedResults.Ok(Result(await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposePurchaseOrder");
        group.MapPost("/proposals/query", async (PurchaseOrderListInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new PurchaseOrderHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Proposal)], Id(row["nextProposalId"])));
        }).WithName("PurchaseOrderHistory");
        group.MapPost("/proposals/{id}/query", async (string id, PurchaseOrderReadDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Proposal(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("PurchaseOrderRead");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, PurchaseOrderDecisionPreviewInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new PurchaseOrderDecisionPreviewDto(Proposal(row["proposal"]), row["decision"].AsString, Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PurchaseOrderDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, PurchaseOrderDecideDto request, HttpContext http, [FromServices] PurchaseOrderService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecidePurchaseOrder");
        group.MapPost("/operations/query", async (PurchaseOrderRecoveryInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PurchaseOrderRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new PurchaseOrderRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverPurchaseOrder");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "purchase-order-review") throw PurchaseOrderRules.Invalid(); }
    private static PurchaseOrderDecision Decision(string value) => value switch { "approve" => PurchaseOrderDecision.Approve, "reject" => PurchaseOrderDecision.Reject, _ => throw PurchaseOrderRules.Invalid() };
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw PurchaseOrderRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static string? OptionalNumber(BsonValue value) => value.IsBsonNull ? null : Number(value);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static PurchaseOrderInputDto Input(BsonValue value) => new(R.Location(value["location"]), new(value["requestId"].AsInt64), Number(value["requestVersion"]), new(value["requestDecisionId"].AsInt64),
        new(value["supplierId"].AsInt64), new(value["supplierVersionId"].AsInt64), Number(value["supplierVersion"]), value["businessDate"].AsString, value["currency"].AsString,
        new(value["commercialReference"]["kind"].AsString, value["commercialReference"]["reference"].AsString, value["commercialReference"]["issuedDate"].AsString, Date(value["commercialReference"]["validUntil"])),
        Text(value["externalOrderReference"]), new(value["paymentTerms"]["kind"].AsString, value["paymentTerms"]["description"].AsString, Amount(value["paymentTerms"]["requiredAdvance"]),
            value["paymentTerms"]["balanceTrigger"].AsString, Text(value["paymentTerms"]["dueDate"])),
        [.. value["lines"].AsBsonArray.Select(line => new PurchaseOrderLineInputDto(new(line["requestSkuId"].AsInt64), line["packages"].AsInt32, line["sourceSystem"].AsString,
            line["sourceAccountReference"].AsString, line["externalItemCode"].AsString, new(line["definitionId"].AsInt64), Number(line["definitionVersion"]),
            Amount(line["netPricePerPackage"]), Amount(line["taxAmount"]), Amount(line["supplierChargeAmount"])))]);
    private static PurchaseOrderRequestDto Request(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), new(value["decisionId"].AsInt64),
        value["route"].AsString, value["kind"].AsString, value["businessDate"].AsString, Text(value["externalReference"]), value["reason"].AsString, R.Source(value["source"]),
        [.. value["lines"].AsBsonArray.Select(line => { var p = R.Package(line); return new PurchaseOrderRequestLineDto(p.SkuId, p.SkuRevision, p.DefinitionId, p.DefinitionVersion,
            p.SkuCode, p.FamilyName, p.PackageLabel, p.PackageKind, p.BaseUnitCode, p.BaseUnitsPerPackage, line["packages"].AsInt32, Text(line["specification"]), line["committedPackages"].AsInt32, line["availablePackages"].AsInt32); })]);
    private static PurchaseOrderRequestReferenceDto RequestReference(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), new(value["decisionId"].AsInt64));
    internal static PurchaseOrderSupplierDto Supplier(BsonValue value) => new(new(value["id"].AsInt64), new(value["versionId"].AsInt64), Number(value["version"]), value["subjectReference"].AsString, value["displayName"].AsString);
    internal static PurchaseOrderLineDto[] Lines(BsonValue value) => [.. value.AsBsonArray.Select(line => { var p = R.Package(line); return new PurchaseOrderLineDto(p.SkuId, p.SkuRevision, p.DefinitionId, p.DefinitionVersion,
        p.SkuCode, p.FamilyName, p.PackageLabel, p.PackageKind, p.BaseUnitCode, p.BaseUnitsPerPackage, line["packages"].AsInt32, Text(line["specification"]), line["sourceSystem"].AsString,
        line["sourceAccountReference"].AsString, line["externalItemCode"].AsString, Amount(line["netPricePerPackage"]), Amount(line["taxAmount"]), Amount(line["supplierChargeAmount"]),
        Amount(line["netAmount"]), Amount(line["totalAmount"]), Number(line["baseUnits"])); })];
    private static PurchaseOrderTotalsDto Totals(BsonValue value) => new(Amount(value["net"]), Amount(value["tax"]), Amount(value["supplierCharges"]), Amount(value["total"]));
    private static PurchaseOrderCapacityDto[] Capacity(BsonValue value) => [.. value.AsBsonArray.Select(row => new PurchaseOrderCapacityDto(new(row["requestSkuId"].AsInt64), row["requestedPackages"].AsInt32,
        row["committedPackages"].AsInt32, row["availablePackages"].AsInt32, Number(row["version"])) )];
    private static PurchaseOrderPreviewDto Preview(BsonValue value) => new(Input(value["input"]), value["route"].AsString, R.Source(value["source"]), RequestReference(value["request"]), Supplier(value["supplier"]),
        Lines(value["lines"]), Totals(value["totals"]), Capacity(value["capacity"]), value["binding"].AsString, Date(value["observedAt"]));
    private static PurchaseOrderDecisionDto DecisionView(BsonValue value) => new(new(value["id"].AsInt64), value["decision"].AsString, value["reason"].AsString, Date(value["decidedAt"]), Id(value["orderId"]), OptionalNumber(value["orderVersion"]));
    private static PurchaseOrderProposalDto Proposal(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), Input(value["input"]), value["route"].AsString, R.Source(value["source"]),
        RequestReference(value["request"]), Supplier(value["supplier"]), Lines(value["lines"]), Totals(value["totals"]), Capacity(value["capacity"]), value["reason"].AsString,
        Date(value["proposedAt"]), value["state"].AsString, value["decision"].IsBsonNull ? null : DecisionView(value["decision"]));
    private static PurchaseOrderResultDto Result(BsonValue value) => new(new(value["proposalId"].AsInt64), Number(value["proposalVersion"]), Id(value["decisionId"]), Id(value["orderId"]), OptionalNumber(value["orderVersion"]), value["status"].AsString);
}

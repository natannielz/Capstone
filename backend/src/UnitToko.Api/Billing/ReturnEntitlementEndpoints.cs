using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Billing;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementLineInputDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId, [property: JsonRequired] Money Tax, [property: JsonRequired] Money Charges);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementInputDto([property: JsonRequired] ReturnEntitlementLineInputDto[] Lines) {
    internal ReturnEntitlementInput Domain() {
        if (Lines is null || Lines.Any(line => line is null)) throw ReturnEntitlementPolicy.Invalid();
        return ReturnEntitlementPolicy.Copy(new([.. Lines.Select(line => new ReturnEntitlementLineInput(line.ReceiptId.Value, line.SkuId.Value, line.Tax, line.Charges))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementPreviewInputDto([property: JsonRequired] string Action, [property: JsonRequired] ReturnEntitlementInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementCommandDto([property: JsonRequired] string Action, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string Binding,
    [property: JsonRequired] ReturnEntitlementInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnEntitlementRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, [property: JsonRequired] string RequestKey);
public sealed record ReturnEntitlementLineDto(LongId ReceiptId, LongId SkuId, string AttributedBaseUnits, string Net, string Tax, string Charges);
public sealed record ReturnEntitlementResultDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Action, string Version, DateTimeOffset RequestedAt,
    string Net, string Tax, string Charges, string Credit, ImmutableArray<ReturnEntitlementLineDto> Lines, string State, string Operation);
public sealed record ReturnEntitlementSourceLineDto(LongId ReceiptId, LongId SkuId, string InspectedBaseUnits, string Identity, string Condition,
    string IssuedBaseUnits, string IssuedNet, string IssuedTax, string IssuedCharges, string PreviouslyAttributedBaseUnits, string PreviouslyAttributedNet, string PreviouslyAttributedTax, string PreviouslyAttributedCharges);
public sealed record ReturnEntitlementViewDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Action, string Version, string BuyerName,
    ImmutableArray<ReturnEntitlementSourceLineDto> Lines, ReturnEntitlementResultDto? Request);
public sealed record ReturnEntitlementPreviewDto(ReturnEntitlementViewDto View, string Binding, string AttributionProcedureReference, string TaxTreatmentReference,
    string ChargeTreatmentReference, string CreditNoteProcedureReference, string Net, string Tax, string Charges, string Credit, ImmutableArray<ReturnEntitlementLineDto> Lines);
public sealed record ReturnEntitlementRecoveryDto(string Status, ReturnEntitlementResultDto? Result);

public static class ReturnEntitlementEndpoints
{
    public static void MapReturnEntitlementEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/cases/{id}/returns/{returnId}/entitlement").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, string returnId, ReturnEntitlementQueryDto request, HttpContext http, ReturnEntitlementService service, CancellationToken ct) => {
            if (request.Purpose != "return-entitlement") throw ReturnEntitlementPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, ct)));
        }).WithName("ReadReturnEntitlement");
        group.MapPost("/preview", async (string id, string returnId, ReturnEntitlementPreviewInputDto request, HttpContext http, ReturnEntitlementService service, CancellationToken ct) => {
            if (request.Input is null) throw ReturnEntitlementPolicy.Invalid(); A.Private(http);
            var preview = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, request.Input.Domain(), ct);
            return TypedResults.Ok(new ReturnEntitlementPreviewDto(View(preview.View), preview.Binding, preview.Policy["attributionProcedureReference"].AsString, preview.Policy["taxTreatmentReference"].AsString,
                preview.Policy["chargeTreatmentReference"].AsString, preview.Policy["creditNoteProcedureReference"].AsString, preview.Result.Net.ToString(), preview.Result.Tax.ToString(), preview.Result.Charges.ToString(), preview.Result.Credit.ToString(),
                [.. preview.Result.Lines.Select(line => new ReturnEntitlementLineDto(new(line.ReceiptId), new(line.SkuId), A.Number(line.AttributedBaseUnits), line.Net.ToString(), line.Tax.ToString(), line.Charges.ToString()))]));
        }).WithName("PreviewReturnEntitlement");
        group.MapPost("", async (string id, string returnId, ReturnEntitlementCommandDto request, HttpContext http, ReturnEntitlementService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw ReturnEntitlementPolicy.Invalid(); var version = PaymentAllocationInputDto.Version(request.ExpectedVersion);
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != version) throw ReturnEntitlementPolicy.Invalid(); A.Private(http);
            var row = await service.RequestAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, version, request.Binding, request.Input.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(row));
        }).WithName("RequestReturnEntitlement");
        group.MapPost("/operations/query", async (string id, string returnId, ReturnEntitlementRecoveryInputDto request, HttpContext http, ReturnEntitlementService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw ReturnEntitlementPolicy.Invalid(); A.Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, request.RequestKey, ct);
            return TypedResults.Ok(new ReturnEntitlementRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverReturnEntitlement");
    }
    private static ImmutableArray<ReturnEntitlementLineDto> Lines(BsonArray lines) => [.. lines.Select(line => new ReturnEntitlementLineDto(new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64), A.Number(line["attributedBaseUnits"].AsInt64),
        A.MoneyText(line["net"]), A.MoneyText(line["tax"]), A.MoneyText(line["charges"])) )];
    internal static ReturnEntitlementResultDto Result(BsonDocument row) => new(new(row["returnId"].AsInt64), new(row["caseId"].AsInt64), new(row["invoiceId"].AsInt64), row["action"].AsString, A.Number(row["version"].AsInt64),
        new(row["requestedAt"].ToUniversalTime()), A.MoneyText(row["net"]), A.MoneyText(row["tax"]), A.MoneyText(row["charges"]), A.MoneyText(row["credit"]), Lines(row["lines"].AsBsonArray), row["state"].AsString, row["operation"].AsString);
    private static ReturnEntitlementViewDto View(ReturnEntitlementView view) {
        var source = view.Source;
        return new(new(source["returnId"].AsInt64), new(source["caseId"].AsInt64), new(source["invoiceId"].AsInt64), source["action"].AsString, A.Number(view.Pool["version"].AsInt64), source["invoice"]["buyerName"].AsString,
            [.. source["inspectionLines"].AsBsonArray.Select(line => {
                var issued = source["issuedLines"].AsBsonArray.Single(value => value["receiptId"] == line["receiptId"] && value["skuId"] == line["skuId"]);
                var prior = view.Pool["lines"].AsBsonArray.Single(value => value["receiptId"] == line["receiptId"] && value["skuId"] == line["skuId"]);
                return new ReturnEntitlementSourceLineDto(new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64), A.Number(line["inspectedBaseUnits"].AsInt64), line["identity"].AsString, line["condition"].AsString,
                    A.Number(issued["acceptedBaseUnits"].AsInt64), A.MoneyText(issued["net"]), A.MoneyText(issued["tax"]), A.MoneyText(issued["charges"]), A.Number(prior["attributedBaseUnits"].AsInt64),
                    A.MoneyText(prior["net"]), A.MoneyText(prior["tax"]), A.MoneyText(prior["charges"]));
            })], view.Request is null ? null : Result(view.Request));
    }
}

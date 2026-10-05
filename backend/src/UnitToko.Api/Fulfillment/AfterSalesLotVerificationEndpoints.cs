using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnedLotVerificationLineDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId,
    [property: JsonRequired] LongId OriginalLotId, [property: JsonRequired] string VerifiedBaseUnits);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnedLotVerificationRequestDto([property: JsonRequired] string Binding, [property: JsonRequired] string ObservedLotLabel,
    [property: JsonRequired] bool ConfirmPhysicalOriginalLotMatch, [property: JsonRequired] ImmutableArray<ReturnedLotVerificationLineDto> Lines, [property: JsonRequired] string Reason)
{
    public ReturnedLotVerificationInput Domain()
    {
        if (Lines.IsDefaultOrEmpty || Lines.Length > 100 || Lines.Any(line => line is null)) throw ReturnedLotVerificationPolicy.Invalid();
        // This is the private label recorded in the signed manual finding's immutable reference.
        // Neither the API nor the current core claims an automatic human-label-to-original-ID match.
        return ReturnedLotVerificationPolicy.Copy(new(ObservedLotLabel, ConfirmPhysicalOriginalLotMatch, [.. Lines.Select(line =>
            new ReturnedLotVerificationLine(line.ReceiptId.Value, line.SkuId.Value, line.OriginalLotId.Value, LongId.Parse(line.VerifiedBaseUnits).Value))]));
    }
}
public sealed record ReturnedLotChoiceDto(LongId OriginalLotId, LongId PoolId, LongId DefinitionId, string PackageLabel, string BaseUnitCode, int BaseUnitsPerPackage, string DispatchedBaseUnits);
public sealed record ReturnedLotLineReviewDto(LongId ReceiptId, LongId SkuId, string ReceivedBaseUnits, string VerifiedBaseUnits, string ManualBaseUnits,
    string State, string Explanation, ImmutableArray<ReturnedLotChoiceDto> Choices);
public sealed record ReturnedLotVerificationDto(LongId CaseId, LongId ReturnId, string Revision, string Binding, string State, ImmutableArray<ReturnedLotLineReviewDto> Lines);
public sealed record ReturnedLotVerificationResultDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Revision, DateTimeOffset VerifiedAt, string State, string Operation);
public sealed record ReturnedLotVerificationRecoveryDto(string Status, ReturnedLotVerificationResultDto? Result);
public static class AfterSalesLotVerificationEndpoints
{
    public static void MapAfterSalesLotVerificationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/fulfillment/cases/{id}/returns/{returnId}/lot-verification").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, string returnId, OrderReadDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (request.Purpose != "after-sales-return-lot-verification" || AccessRequestMetadata.ExpectedVersion(http.Request) != 2) throw ReturnedLotVerificationPolicy.Invalid();
            var value = await service.ReadLotVerificationAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, ct);
            http.Response.Headers.ETag = "\"" + Number(value.Revision) + "\"";
            return TypedResults.Ok(new ReturnedLotVerificationDto(value.CaseId, value.ReturnId, Number(value.Revision), value.Binding, value.State,
                [.. value.Lines.Select(line => new ReturnedLotLineReviewDto(line.ReceiptId, line.SkuId, Number(line.ReceivedBaseUnits), Number(line.VerifiedBaseUnits), Number(line.ManualBaseUnits),
                    line.State, line.Explanation, [.. line.Choices.Select(choice => new ReturnedLotChoiceDto(choice.OriginalLotId, choice.PoolId, choice.DefinitionId,
                        choice.PackageLabel, choice.BaseUnitCode, choice.BaseUnitsPerPackage, Number(choice.DispatchedBaseUnits)))]))]));
        }).WithName("ReadAfterSalesReturnLotVerification");
        group.MapPost("", async (string id, string returnId, ReturnedLotVerificationRequestDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); var input = request.Domain();
            return TypedResults.Ok(Result(await service.VerifyReturnedLotAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Binding, input, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("VerifyAfterSalesReturnedLot");
        group.MapPost("/operations/query", async (string id, string returnId, OrderDraftRecoveryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            Private(http); if (request.Purpose != "operation-recovery") throw ReturnedLotVerificationPolicy.Invalid();
            var value = await service.RecoverLotVerificationAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Key, ct);
            return TypedResults.Ok(new ReturnedLotVerificationRecoveryDto(value is null ? "not_confirmed" : "committed", value is null ? null : Result(value)));
        }).WithName("RecoverAfterSalesReturnedLotVerification");
    }
    private static ReturnedLotVerificationResultDto Result(BsonDocument value) => new(new(value["returnId"].AsInt64), new(value["caseId"].AsInt64), new(value["invoiceId"].AsInt64),
        Number(value["revision"].AsInt64), new(value["verifiedAt"].ToUniversalTime()), value["state"].AsString, value["operation"].AsString);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptRecipientQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string? BeforeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptChallengeCommandDto([property: JsonRequired] LongId DocumentId, [property: JsonRequired] LongId RecipientId, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptChallengeReviewDto([property: JsonRequired] string Token);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptAmountDto([property: JsonRequired] int Packages, [property: JsonRequired] int LooseUnits);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptDeclarationDto([property: JsonRequired] LongId SkuId, [property: JsonRequired] ReceiptAmountDto Accepted,
    [property: JsonRequired] ReceiptAmountDto Rejected, [property: JsonRequired] ReceiptAmountDto Missing, [property: JsonRequired] string? ExceptionReason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptConfirmDto([property: JsonRequired] string Token, [property: JsonRequired] string ConsentVersion, [property: JsonRequired] bool Consent,
    [property: JsonRequired] ReceiptDeclarationDto[] Lines, [property: JsonRequired] string Reason);
public sealed record ReceiptLineDto(LongId SkuId, LongId DefinitionId, ReceiptAmountDto Accepted, ReceiptAmountDto Rejected, ReceiptAmountDto Missing, string? ExceptionReason,
    string DispatchedBaseUnits, string AcceptedBaseUnits, string RejectedBaseUnits, string MissingBaseUnits);
public sealed record ReceiptDto(LongId Id, LongId ShipmentId, LongId DocumentId, LongId RecipientId, string RecipientName, DateTimeOffset RecordedAt, bool HasException, ImmutableArray<ReceiptLineDto> Lines);
public sealed record ReceiptStateDto(string Status, ReceiptDto? Receipt);
public sealed record ReceiptChallengeStateDto(string Status, ShipmentReceiptChallengeView? Challenge);
public sealed record ReceiptReviewDto(LongId ChallengeId, LongId ShipmentId, LongId DocumentId, string DocumentVersion, string DocumentHash, string RecipientName,
    DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, string ConsentVersion, string ConsentText, ImmutableArray<ShipmentReceiptReviewLine> Lines);

public static class ShipmentReceiptEndpoints
{
    public static void MapShipmentReceiptEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/receipts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/{shipmentId}/recipients/query", async (string shipmentId, ReceiptRecipientQueryDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(await service.RecipientsAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, request.BeforeId is null ? null : LongId.Parse(request.BeforeId).Value, ct));
        }).WithName("ListShipmentReceiptRecipients");
        group.MapPost("/{shipmentId}/challenges", async (string shipmentId, ReceiptChallengeCommandDto request, HttpContext http, ShipmentReceiptService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => TypedResults.Ok(await service.IssueChallengeAsync(http.Features.Get<ResolvedActor>()!.Actor,
                LongId.Parse(shipmentId).Value, request.DocumentId.Value, request.RecipientId.Value, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct))).WithName("IssueShipmentReceiptChallenge");
        group.MapPost("/challenges/{challengeId}/query", async (string challengeId, ReceiptChallengeReviewDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            var review = await service.ReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(challengeId).Value, request.Token, ct);
            return TypedResults.Ok(new ReceiptReviewDto(review.ChallengeId, review.ShipmentId, review.DocumentId, Number(review.DocumentVersion), review.DocumentHash, review.RecipientName,
                review.ExpiresAt, review.ServerTime, review.ConsentVersion, review.ConsentText, review.Lines));
        }).WithName("ReviewShipmentReceiptChallenge");
        group.MapPost("/challenges/{challengeId}/confirm", async (string challengeId, ReceiptConfirmDto request, HttpContext http, ShipmentReceiptService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Lines is null || request.Lines.Length is < 1 or > 100 || request.Lines.Any(line => line is null || line.Accepted is null || line.Rejected is null || line.Missing is null)) throw Invalid();
            var lines = request.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Accepted.Packages, line.Accepted.LooseUnits), new(line.Rejected.Packages, line.Rejected.LooseUnits),
                new(line.Missing.Packages, line.Missing.LooseUnits), line.ExceptionReason)).ToImmutableArray();
            return TypedResults.Ok(View(await service.ConfirmAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(challengeId).Value, request.Token, request.ConsentVersion, request.Consent, lines,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ConfirmShipmentReceipt");
        group.MapPost("/{shipmentId}/query", async (string shipmentId, OrderReadDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var receipt = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, ct);
            return TypedResults.Ok(new ReceiptStateDto(receipt is null ? "not_received" : "received", receipt is null ? null : View(receipt)));
        }).WithName("ReadShipmentReceipt");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var receipt = await service.RecoverConfirmationAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new ReceiptStateDto(receipt is null ? "not_confirmed" : "committed", receipt is null ? null : View(receipt)));
        }).WithName("RecoverShipmentReceipt");
        group.MapPost("/challenge-operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var challenge = await service.RecoverChallengeAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new ReceiptChallengeStateDto(challenge is null ? "not_confirmed" : "committed", challenge));
        }).WithName("RecoverShipmentReceiptChallenge");
    }
    private static ReceiptDto View(ShipmentReceiptView receipt) => new(receipt.Id, receipt.ShipmentId, receipt.DocumentId, receipt.RecipientId, receipt.RecipientName, receipt.RecordedAt, receipt.HasException,
        receipt.Lines.Select(line => new ReceiptLineDto(line.Declaration.SkuId, line.DefinitionId, Amount(line.Declaration.Accepted), Amount(line.Declaration.Rejected), Amount(line.Declaration.Missing),
            line.Declaration.ExceptionReason, Number(line.DispatchedBaseUnits), Number(line.AcceptedBaseUnits), Number(line.RejectedBaseUnits), Number(line.MissingBaseUnits))).ToImmutableArray());
    private static ReceiptAmountDto Amount(ReceiptPackageAmount value) => new(value.Packages, value.LooseUnits);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "shipment-receipt") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_delivery_receipt", "Use the authorized delivery receipt workflow");
}

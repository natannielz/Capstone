using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryAttemptQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string? BeforeSequence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryAttemptCommandDto([property: JsonRequired] string Outcome, [property: JsonRequired] DateTimeOffset OccurredAt, [property: JsonRequired] string Reason);
public sealed record DeliveryAttemptDto(LongId Id, LongId ShipmentId, string Sequence, string Outcome, string Reason, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, int RevokedQrCount);
public sealed record DeliveryAttemptPageDto(LongId ShipmentId, string Sequence, bool Received, ImmutableArray<DeliveryAttemptDto> Items, bool HasMore);
public sealed record DeliveryAttemptPreviewDto(LongId ShipmentId, string Sequence, bool Received, DateTimeOffset DispatchedAt, DateTimeOffset ServerTime, DateTimeOffset? LastOccurredAt);
public sealed record DeliveryAttemptRecoveryDto(string Status, DeliveryAttemptDto? Attempt);
public static class DeliveryAttemptEndpoints
{
    public static void MapDeliveryAttemptEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/delivery-attempts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/{shipmentId}/query", async (string shipmentId, DeliveryAttemptQueryDto request, HttpContext http, DeliveryAttemptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, request.BeforeSequence is null ? null : LongId.Parse(request.BeforeSequence).Value, ct);
            return TypedResults.Ok(new DeliveryAttemptPageDto(page.ShipmentId, Number(page.Sequence), page.Received, page.Items.Select(View).ToImmutableArray(), page.HasMore));
        }).WithName("ReadDeliveryAttempts");
        group.MapPost("/{shipmentId}/preview", async (string shipmentId, OrderReadDto request, HttpContext http, DeliveryAttemptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, ct);
            return TypedResults.Ok(new DeliveryAttemptPreviewDto(value.ShipmentId, Number(value.Sequence), value.Received, value.DispatchedAt, value.ServerTime, value.LastOccurredAt));
        }).WithName("PreviewDeliveryAttempt");
        group.MapPost("/{shipmentId}", async (string shipmentId, DeliveryAttemptCommandDto request, HttpContext http, DeliveryAttemptService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => TypedResults.Ok(View(await service.RecordAsync(http.Features.Get<ResolvedActor>()!.Actor,
                LongId.Parse(shipmentId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Outcome, request.OccurredAt,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)))).WithName("RecordDeliveryAttempt");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, DeliveryAttemptService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var attempt = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new DeliveryAttemptRecoveryDto(attempt is null ? "not_confirmed" : "committed", attempt is null ? null : View(attempt)));
        }).WithName("RecoverDeliveryAttempt");
    }
    private static DeliveryAttemptDto View(DeliveryAttemptView row) => new(row.Id, row.ShipmentId, Number(row.Sequence), row.Outcome, row.Reason, row.OccurredAt, row.RecordedAt, row.RevokedQrCount);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "delivery-attempt") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_delivery_attempt", "Use the current authorized delivery attempt workflow");
}

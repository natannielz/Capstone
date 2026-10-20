using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;

public sealed record ShipmentCurrentCourierDto(string Revision, LongId CourierId, string CourierName, DateTimeOffset EffectiveAt);
public sealed record ShipmentCourierChangeDto(LongId Id, LongId ShipmentId, string Revision, LongId PreviousCourierId,
    LongId CourierId, string CourierName, DateTimeOffset RecordedAt, string Reason);
public sealed record ShipmentCourierTaskDto(LongId ShipmentId, LongId OrderId, LongId SuborderId, LongId NumberEntity, LongId NumberChannel,
    string NumberYear, string Number, LongId DepartureCourierId, string DepartureCourierName, ShipmentCurrentCourierDto CurrentAssignment,
    bool Received, bool CanHandover, ShipmentCourierChangeDto[] History);
public sealed record ShipmentCourierTasksDto(ShipmentCourierTaskDto[] Items, bool HasMore);
public sealed record ShipmentCourierPreviewDto(ShipmentCourierTaskDto Task, LongId CourierId, string CourierName, string Review);
public sealed record ShipmentCourierRecoveryDto(string Status, ShipmentCourierChangeDto? Result);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShipmentCourierPreviewRequest([property: JsonRequired] string Purpose, [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] LongId CourierId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShipmentCourierCommandRequest([property: JsonRequired] string ExpectedRevision, [property: JsonRequired] LongId CourierId,
    [property: JsonRequired] string Review, [property: JsonRequired] string Reason);

public static class ShipmentCourierEndpoints
{
    public static void MapShipmentCourierEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/courier-assignments").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (PreparationPageRequest request, HttpContext http, ShipmentCourierService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.ListAsync(Actor(http), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ShipmentCourierTasksDto([.. page.Items.Select(Task)], page.HasMore));
        }).WithName("ListShipmentCourierAssignments");
        group.MapPost("/{shipmentId}/query", async (string shipmentId, OrderReadDto request, HttpContext http, ShipmentCourierService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Task(await service.ReadAsync(Actor(http), LongId.Parse(shipmentId).Value, ct)));
        }).WithName("ReadShipmentCourierAssignment");
        group.MapPost("/{shipmentId}/couriers/query", async (string shipmentId, PreparationPageRequest request, HttpContext http, ShipmentCourierService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.CouriersAsync(Actor(http), LongId.Parse(shipmentId).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new DispatchCouriersDto([.. page.Items.Select(row => new DispatchCourierDto(row.UserId, row.DisplayName))], page.HasMore));
        }).WithName("ListShipmentHandoverCouriers");
        group.MapPost("/{shipmentId}/preview/query", async (string shipmentId, ShipmentCourierPreviewRequest request, HttpContext http, ShipmentCourierService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewAsync(Actor(http), LongId.Parse(shipmentId).Value,
                LongId.Parse(request.ExpectedRevision).Value, request.CourierId.Value, ct);
            return TypedResults.Ok(new ShipmentCourierPreviewDto(Task(row.Task), row.CourierId, row.CourierName, row.Review));
        }).WithName("PreviewShipmentCourierHandover");
        group.MapPost("/{shipmentId}/handover", async (string shipmentId, ShipmentCourierCommandRequest request, HttpContext http, ShipmentCourierService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var revision = LongId.Parse(request.ExpectedRevision).Value; if (AccessRequestMetadata.ExpectedVersion(http.Request) != revision) throw Invalid();
            var row = await service.HandoverAsync(Actor(http), LongId.Parse(shipmentId).Value, revision, request.CourierId.Value, request.Review,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.Revision); return TypedResults.Ok(Change(row));
        }).WithName("HandoverShipmentCourier");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentCourierService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var row = await service.RecoverAsync(Actor(http), request.Key, ct);
            return TypedResults.Ok(new ShipmentCourierRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Change(row)));
        }).WithName("RecoverShipmentCourierHandover");
    }
    internal static ShipmentCurrentCourierDto Current(ShipmentCurrentCourier row) => new(Number(row.Revision), row.CourierId, row.CourierName, row.EffectiveAt);
    private static ShipmentCourierChangeDto Change(ShipmentCourierChange row) => new(row.Id, row.ShipmentId, Number(row.Revision), row.PreviousCourierId,
        row.CourierId, row.CourierName, row.RecordedAt, row.Reason);
    private static ShipmentCourierTaskDto Task(ShipmentCourierTask row) => new(row.ShipmentId, row.OrderId, row.SuborderId, row.NumberEntity, row.NumberChannel,
        Number(row.NumberYear), Number(row.Number), row.DepartureCourierId, row.DepartureCourierName, Current(row.CurrentAssignment), row.Received, row.CanHandover, [.. row.History.Select(Change)]);
    private static UnitToko.Modules.Identity.Contracts.ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()!.Actor;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string purpose) { if (purpose != "courier-assignment") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_courier_assignment_request", "Review the current courier and explicitly confirm the task handover");
}

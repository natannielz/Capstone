using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShipmentDocumentCommandDto([property: JsonRequired] string Reason);
public sealed record ShipmentDocumentDto(LongId Id, LongId ShipmentId, string ShipmentRevision, string Version, string ContentHash, string ByteLength, DateTimeOffset IssuedAt);
public sealed record ShipmentDocumentStateDto(string Status, ShipmentDocumentDto? Document);
public static class ShipmentDocumentEndpoints
{
    public static void MapShipmentDocumentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/shipment-documents").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/{shipmentId}/query", async (string shipmentId, OrderReadDto request, HttpContext http, ShipmentDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var document = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, ct);
            return TypedResults.Ok(new ShipmentDocumentStateDto(document is null ? "not_issued" : "issued", document is null ? null : View(document)));
        }).WithName("ReadShipmentDocument");
        group.MapPost("/{shipmentId}", async (string shipmentId, ShipmentDocumentCommandDto request, HttpContext http, ShipmentDocumentService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var document = await service.IssueAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(View(document));
        }).WithName("IssueShipmentDocument");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentDocumentService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var document = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new ShipmentDocumentStateDto(document is null ? "not_confirmed" : "committed", document is null ? null : View(document)));
        }).WithName("RecoverShipmentDocument");
        group.MapPost("/{shipmentId}/{documentId}/download", async (string shipmentId, string documentId, OrderReadDto request, HttpContext http, ShipmentDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var file = await service.DownloadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, LongId.Parse(documentId).Value, ct);
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return TypedResults.File(Encoding.UTF8.GetBytes(file.Content), "text/html; charset=utf-8", file.FileName);
        }).WithName("DownloadShipmentDocument").Produces<byte[]>(200, "text/html");
    }
    private static ShipmentDocumentDto View(ShipmentDocumentView row) => new(row.Id, row.ShipmentId, Number(row.ShipmentRevision), Number(row.Version), row.ContentHash, Number(row.ByteLength), row.IssuedAt);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "shipment-document") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_shipment_document", "Use the current authorized shipment document");
}

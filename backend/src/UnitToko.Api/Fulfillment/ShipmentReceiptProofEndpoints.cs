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
public sealed record ReceiptProofReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] bool AsRecipient);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceiptProofCommandDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] bool AsRecipient, [property: JsonRequired] string Reason);
public sealed record ReceiptProofDto(LongId Id, LongId ShipmentId, LongId ReceiptId, string ShipmentRevision, string Version, string ContentHash, string ByteLength, DateTimeOffset IssuedAt, bool IssuedAsRecipient);
public sealed record ReceiptProofStateDto(string Status, ReceiptProofDto? Document);
public static class ShipmentReceiptProofEndpoints
{
    public static void MapShipmentReceiptProofEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/receipt-proofs").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/{shipmentId}/query", async (string shipmentId, ReceiptProofReadDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var document = await service.ReadProofAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, request.AsRecipient, ct);
            return TypedResults.Ok(new ReceiptProofStateDto(document is null ? "not_issued" : "issued", document is null ? null : View(document)));
        }).WithName("ReadShipmentReceiptProof");
        group.MapPost("/{shipmentId}", async (string shipmentId, ReceiptProofCommandDto request, HttpContext http, ShipmentReceiptService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => TypedResults.Ok(View(await service.IssueProofAsync(http.Features.Get<ResolvedActor>()!.Actor,
                LongId.Parse(shipmentId).Value, request.ReceiptId.Value, request.AsRecipient, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)))).WithName("IssueShipmentReceiptProof");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var document = await service.RecoverProofAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new ReceiptProofStateDto(document is null ? "not_confirmed" : "committed", document is null ? null : View(document)));
        }).WithName("RecoverShipmentReceiptProof");
        group.MapPost("/{shipmentId}/{documentId}/download", async (string shipmentId, string documentId, ReceiptProofReadDto request, HttpContext http, ShipmentReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var file = await service.DownloadProofAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(shipmentId).Value, LongId.Parse(documentId).Value, request.AsRecipient, ct);
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return TypedResults.File(Encoding.UTF8.GetBytes(file.Content), "text/html; charset=utf-8", file.FileName);
        }).WithName("DownloadShipmentReceiptProof").Produces<byte[]>(200, "text/html");
    }
    private static ReceiptProofDto View(ShipmentReceiptProofView row) => new(row.Id, row.ShipmentId, row.ReceiptId, Number(row.ShipmentRevision), Number(row.Version), row.ContentHash, Number(row.ByteLength), row.IssuedAt, row.IssuedAsRecipient);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "receipt-proof") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_receipt_proof", "Use the authorized receipt proof workflow");
}

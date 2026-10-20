using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Api.Authorization;
using UnitToko.Api.Fulfillment;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderTimelineQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] LongId? BeforeId, [property: JsonRequired] int Limit);
public sealed record OrderTimelineShipmentDto(LongId Id, string Revision, LongId SuborderId, string Number, DateTimeOffset DispatchedAt, LongId CourierId, string CourierName,
    ShipmentCurrentCourierDto CurrentAssignment);
public sealed record OrderTimelineInvoiceDto(LongId Id, string Number, DateTimeOffset IssuedAt, DateOnly DueDate, LongId[] OrderIds);
public sealed record OrderTimelinePaymentDto(LongId Id, string Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string State, LongId[] InvoiceIds, DateTimeOffset? SubmittedAt);
public sealed record OrderTimelineCaseDto(LongId Id, string Revision, DateTimeOffset RequestedAt, string State, LongId InvoiceId, string Issue);
public sealed record OrderTimelineShipmentPageDto(LongId OrderId, OrderTimelineShipmentDto[] Items, bool HasMore);
public sealed record OrderTimelineInvoicePageDto(LongId OrderId, OrderTimelineInvoiceDto[] Items, bool HasMore);
public sealed record OrderTimelinePaymentPageDto(LongId OrderId, OrderTimelinePaymentDto[] Items, bool HasMore);
public sealed record OrderTimelineCasePageDto(LongId OrderId, OrderTimelineCaseDto[] Items, bool HasMore);

public static class OrderTimelineEndpoints
{
    public static void MapOrderTimelineEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/orders/{orderId}/timeline").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/shipments/query", async (string orderId, OrderTimelineQueryDto request, HttpContext http,
            ShipmentQueryService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Query(http, orderId, request);
            var page = await service.ListForOrderAsync(Actor(http), id.Value, request.BeforeId?.Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct);
            return TypedResults.Ok(new OrderTimelineShipmentPageDto(id, [.. page.Items.Select(row =>
                new OrderTimelineShipmentDto(row.Id, Number(row.Revision), row.SuborderId,
                    DocumentNumber(row.NumberEntity, row.NumberChannel, row.NumberYear, row.Number), row.DispatchedAt, row.CourierId, row.CourierName,
                    ShipmentCourierEndpoints.Current(row.CurrentAssignment)))], page.HasMore));
        }).WithName("ListOwnOrderTimelineShipments");
        group.MapPost("/invoices/query", async (string orderId, OrderTimelineQueryDto request, HttpContext http,
            InvoiceService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Query(http, orderId, request);
            var page = await service.ListForOrderAsync(Actor(http), id.Value, request.BeforeId?.Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct);
            return TypedResults.Ok(new OrderTimelineInvoicePageDto(id, [.. page.Items.Select(row =>
                new OrderTimelineInvoiceDto(row.Id, DocumentNumber(row.NumberEntity, row.NumberChannel, row.NumberYear, row.Number),
                    row.IssuedAt, row.Detail.DueDate, [.. row.Detail.Lines.Select(line => line.OrderId).Distinct().OrderBy(value => value.Value)]))], page.HasMore));
        }).WithName("ListOwnOrderTimelineInvoices");
        group.MapPost("/payments/query", async (string orderId, OrderTimelineQueryDto request, HttpContext http,
            PaymentDraftService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Query(http, orderId, request);
            var page = await service.ListForOrderAsync(Actor(http), id.Value, request.BeforeId?.Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct);
            return TypedResults.Ok(new OrderTimelinePaymentPageDto(id, [.. page.Items.Select(row =>
                new OrderTimelinePaymentDto(row.Id, Number(row.Revision), row.CreatedAt, row.UpdatedAt, row.State,
                    [.. row.Invoices.Select(invoice => invoice.InvoiceId)], row.Submission?.SubmittedAt))], page.HasMore));
        }).WithName("ListOwnOrderTimelinePayments");
        group.MapPost("/cases/query", async (string orderId, OrderTimelineQueryDto request, HttpContext http,
            AfterSalesCaseService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Query(http, orderId, request);
            var page = await service.ListForOrderAsync(Actor(http), id.Value, request.BeforeId?.Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct);
            return TypedResults.Ok(new OrderTimelineCasePageDto(id, [.. page.Items.Select(row =>
                new OrderTimelineCaseDto(row.Id, Number(row.Revision), row.RequestedAt, row.State, row.Source.InvoiceId, row.Issue))], page.HasMore));
        }).WithName("ListOwnOrderTimelineCases");
        group.MapPost("/payments/{declarationId}/query", async (string orderId, string declarationId, OrderReadDto request, HttpContext http,
            PaymentDraftService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Detail(http, orderId, request);
            var result = await service.ReadTimelineOutcomeAsync(Actor(http), id.Value, LongId.Parse(declarationId).Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct); return TypedResults.Ok(result);
        }).WithName("ReadOwnOrderTimelinePayment");
        group.MapPost("/cases/{caseId}/query", async (string orderId, string caseId, OrderReadDto request, HttpContext http,
            AfterSalesCaseService service, OrderSubmissionService orders, CancellationToken ct) => {
            var id = Detail(http, orderId, request);
            var result = await service.ReadTimelineOutcomeAsync(Actor(http), id.Value, LongId.Parse(caseId).Value, ct);
            await orders.ReadOwnAsync(Actor(http), id.Value, ct); return TypedResults.Ok(result);
        }).WithName("ReadOwnOrderTimelineCase");
    }
    private static UnitToko.Modules.Identity.Contracts.ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()!.Actor;
    private static LongId Detail(HttpContext http, string orderId, OrderReadDto request) {
        if (request.Purpose != "order-timeline") throw Invalid(); Private(http); return LongId.Parse(orderId);
    }
    private static LongId Query(HttpContext http, string orderId, OrderTimelineQueryDto request) {
        if (request.Purpose != "order-timeline" || request.Limit != 25) throw Invalid();
        Private(http); return LongId.Parse(orderId);
    }
    private static void Private(HttpContext http) {
        http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache";
    }
    private static string DocumentNumber(LongId entity, LongId channel, long year, long number) =>
        string.Join('/', entity.ToString(), channel.ToString(), Number(year), Number(number));
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DomainValidationException Invalid() => new("invalid_order_timeline_request", "Use the current order timeline and its page cursor");
}

using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSubstitutionInputDto([property: JsonRequired] LongId OrderId, [property: JsonRequired] LongId SuborderId,
    [property: JsonRequired] string ExpectedOrderRevision, [property: JsonRequired] string ExpectedDispatchSequence,
    [property: JsonRequired] LongId OriginalSkuId, [property: JsonRequired] int OriginalPackages, [property: JsonRequired] OrderPackageSelectionDto Replacement,
    string OriginalLineRevision = "0")
{
    internal OrderSubstitutionInput Domain() {
        if (Replacement is null || !long.TryParse(ExpectedDispatchSequence, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence < 0 || sequence.ToString(CultureInfo.InvariantCulture) != ExpectedDispatchSequence) throw OrderSubstitutionEndpoints.Invalid();
        var value = new OrderSubstitutionInput(OrderId, SuborderId, CatalogInputs.PositiveVersion(ExpectedOrderRevision), sequence,
            new(OriginalSkuId, OriginalPackages, new(Replacement.SkuId, Replacement.DefinitionId, CatalogInputs.PositiveVersion(Replacement.DefinitionVersion),
                Replacement.PriceId, CatalogInputs.PositiveVersion(Replacement.PriceVersion), Replacement.Packages), OrderLineInputs.Revision(OriginalLineRevision)));
        OrderSubstitutionPolicy.Validate(value); return value;
    }
    internal static OrderSubstitutionInputDto From(OrderSubstitutionInput value) => new(value.OrderId, value.SuborderId,
        OrderDraftInputDto.Number(value.ExpectedOrderRevision), OrderDraftInputDto.Number(value.ExpectedDispatchSequence), value.Selection.OriginalSkuId,
        value.Selection.OriginalPackages, new(value.Selection.Replacement.SkuId, value.Selection.Replacement.DefinitionId, OrderDraftInputDto.Number(value.Selection.Replacement.DefinitionVersion),
            value.Selection.Replacement.PriceId, OrderDraftInputDto.Number(value.Selection.Replacement.PriceVersion), value.Selection.Replacement.Packages),
        OrderDraftInputDto.Number(value.Selection.OriginalLineRevision));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionTargetQueryDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionListQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] bool ForBuyer, LongId? OrderId = null, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionPackagesQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId OrderId,
    [property: JsonRequired] LongId SuborderId, string? Search = null, LongId? AfterId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderSubstitutionInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionProposeDto([property: JsonRequired] OrderSubstitutionInputDto Input, [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionDecisionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionDecisionDto([property: JsonRequired] string Decision, [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubstitutionRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record SubstitutionTargetLineDto(OrderQuoteLineDto Line, int RemainingPackages);
public sealed record SubstitutionTargetDto(LongId OrderId, LongId SuborderId, string Revision, string DispatchSequence, string BuyerName, SubstitutionTargetLineDto[] Lines, bool CanPropose);
public sealed record SubstitutionTargetPageDto(SubstitutionTargetDto[] Items, bool HasMore);
public sealed record SubstitutionPackagePageDto(OrderQuoteLineDto[] Items, bool HasMore);
public sealed record SubstitutionProposalReviewDto(OrderSubstitutionInputDto Input, string Review, string BuyerName, OrderQuoteLineDto Original,
    int RemainingPackages, OrderQuoteLineDto Replacement, DateTimeOffset CalculatedAt);
public sealed record SubstitutionDecisionReviewDto(LongId ProposalId, LongId OrderId, LongId SuborderId, string Revision, string Decision,
    string Review, OrderQuoteLineDto Original, int OriginalPackages, OrderQuoteLineDto Replacement, string Currency, OrderQuoteLineDto[] Lines,
    Money CatalogSubtotal, Money Tax, Money Charges, Money Total, string SpendingState, DateTimeOffset CalculatedAt);
public sealed record SubstitutionHistoryDto(LongId ProposalId, LongId OrderId, LongId SuborderId, string BuyerName, OrderSubstitutionInputDto Input,
    OrderQuoteLineDto Original, OrderQuoteLineDto Replacement, string Status, LongId? DecisionId, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);
public sealed record SubstitutionHistoryPageDto(SubstitutionHistoryDto[] Items, bool HasMore);
public sealed record SubstitutionResultDto(LongId ProposalId, LongId OrderId, LongId SuborderId, LongId? DecisionId, string? Revision, string Status, DateTimeOffset RecordedAt);
public sealed record SubstitutionRecoveryResultDto(string Status, SubstitutionResultDto? Result);

public static class OrderSubstitutionEndpoints
{
    public static void MapOrderSubstitutionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/substitutions").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (SubstitutionTargetQueryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ListSubstitutionTargetsAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SubstitutionTargetPageDto([.. page.Items.Select(value => new SubstitutionTargetDto(value.OrderId, value.SuborderId,
                OrderDraftInputDto.Number(value.Revision), OrderDraftInputDto.Number(value.DispatchSequence), value.BuyerName,
                [.. value.Lines.Select(line => new SubstitutionTargetLineDto(Line(line.Catalog), line.RemainingPackages))], value.CanPropose))], page.HasMore));
        }).WithName("OrderSubstitutionTargets");
        group.MapPost("/packages/query", async (SubstitutionPackagesQueryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.SubstitutionPackagesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.OrderId.Value, request.SuborderId.Value, request.Search, request.AfterId?.Value, ct);
            return TypedResults.Ok(new SubstitutionPackagePageDto([.. page.Items.Select(Line)], page.HasMore));
        }).WithName("OrderSubstitutionPackages");
        group.MapPost("/query", async (SubstitutionListQueryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ListSubstitutionsAsync(http.Features.Get<ResolvedActor>()!.Actor, request.ForBuyer, request.OrderId?.Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SubstitutionHistoryPageDto([.. page.Items.Select(value => new SubstitutionHistoryDto(value.ProposalId, value.OrderId, value.SuborderId,
                value.BuyerName, OrderSubstitutionInputDto.From(value.Input), Line(value.Original), Line(value.Replacement), value.Status, value.DecisionId, value.ProposedAt, value.DecidedAt))], page.HasMore));
        }).WithName("OrderSubstitutionHistory");
        group.MapPost("/preview/query", async (SubstitutionPreviewDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw Invalid();
            var value = await service.PreviewSubstitutionAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Input.Domain(), ct);
            return TypedResults.Ok(new SubstitutionProposalReviewDto(OrderSubstitutionInputDto.From(value.Input), value.Review, value.BuyerName,
                Line(value.Original), value.RemainingPackages, Line(value.Replacement), value.CalculatedAt));
        }).WithName("PreviewOrderSubstitution");
        group.MapPost("", async (SubstitutionProposeDto request, HttpContext http, [FromServices] OrderSubmissionService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null || AccessRequestMetadata.ExpectedVersion(http.Request) != 0) throw Invalid();
            return TypedResults.Ok(Result(await service.ProposeSubstitutionAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Input.Domain(), request.Review,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeOrderSubstitution");
        group.MapPost("/{id}/preview/query", async (string id, SubstitutionDecisionPreviewDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.PreviewSubstitutionDecisionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new SubstitutionDecisionReviewDto(value.ProposalId, value.OrderId, value.SuborderId, OrderDraftInputDto.Number(value.Revision), value.Decision.ToString(),
                value.Review, Line(value.Original), value.OriginalPackages, Line(value.Replacement), value.CatalogAfter.Currency, OrderSubmissionEndpoints.Lines(value.CatalogAfter),
                value.CatalogAfter.CatalogSubtotal, value.Tax, value.Charges, value.Total, value.SpendingState, value.CalculatedAt));
        }).WithName("ReviewOrderSubstitutionConsent");
        group.MapPost("/{id}/decisions", async (string id, SubstitutionDecisionDto request, HttpContext http, [FromServices] OrderSubmissionService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var result = Result(await service.DecideSubstitutionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Review, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
            http.Response.Headers.ETag = '"' + result.Revision + '"'; return TypedResults.Ok(result);
        }).WithName("DecideOrderSubstitution");
        group.MapPost("/operations/query", async (SubstitutionRecoveryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var actor = http.Features.Get<ResolvedActor>()!.Actor;
            var value = request.Operation switch {
                "propose" => await service.RecoverSubstitutionProposalAsync(actor, request.Key, ct),
                "accept" => await service.RecoverSubstitutionDecisionAsync(actor, request.Key, OrderSubstitutionDecision.Accept, ct),
                "decline" => await service.RecoverSubstitutionDecisionAsync(actor, request.Key, OrderSubstitutionDecision.Decline, ct), _ => throw Invalid() };
            return TypedResults.Ok(new SubstitutionRecoveryResultDto(value is null ? "not_confirmed" : "committed", value is null ? null : Result(value)));
        }).WithName("RecoverOrderSubstitution");
    }
    private static OrderQuoteLineDto Line(OrderQuotedLine value) => OrderSubmissionEndpoints.Lines(new([value], value.Price.Currency, value.CatalogSubtotal)).Single();
    private static SubstitutionResultDto Result(BsonDocument row) => new(new(row["proposalId"].AsInt64), new(row["orderId"].AsInt64), new(row["suborderId"].AsInt64),
        row.TryGetValue("decisionId", out var decision) ? new(decision.AsInt64) : null, row.TryGetValue("revision", out var revision) ? OrderDraftInputDto.Number(revision.AsInt64) : null,
        row["status"].AsString, DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static OrderSubstitutionDecision Decision(string value) => value switch { "Accept" => OrderSubstitutionDecision.Accept, "Decline" => OrderSubstitutionDecision.Decline, _ => throw Invalid() };
    private static void Purpose(string value) { if (value != "order-substitution") throw Invalid(); }
    internal static DomainValidationException Invalid() => new("invalid_order_substitution", "Choose and review an explicit replacement proposal");
}

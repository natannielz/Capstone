using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Inventory;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Endpoints;

public static class StockReleaseEndpoints
{
    public static void MapStockReleaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/stock-releases").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store, max-age=0";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        foreach (var status in new[] { 400, 401, 403, 404, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/warehouses/query", async (StockReleaseReadDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-warehouses");
            var choices = await service.LocationsAsync(Actor(http), ct);
            return TypedResults.Ok(new StockSourceWarehousePageDto([.. choices.Select(choice => new StockSourceWarehouseDto(
                AccessScopeDto.From(choice.Location.ToScope()), choice.OrganizationName, choice.ShopName, choice.WarehouseName, choice.ChannelName))]));
        }).WithName("StockReleaseWarehouses");
        group.MapPost("/sources/query", async (StockReleaseSourcesQueryDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-sources");
            return TypedResults.Ok(StockReleaseResponses.Sources(await service.SourcesAsync(Actor(http), StockReleaseInputDto.LocationValue(request.Location), request.Limit, ct)));
        }).WithName("StockReleaseSources");
        group.MapPost("/proposals/query", async (StockReleaseHistoryQueryDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-history");
            return TypedResults.Ok(StockReleaseResponses.History(await service.ListAsync(Actor(http), StockReleaseInputDto.LocationValue(request.Location), request.AfterProposalId?.Value ?? 0, request.Limit, ct)));
        }).WithName("StockReleaseHistory");
        group.MapPost("/preview/query", async (StockReleasePreviewQueryDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-preview");
            var row = await service.PreviewAsync(Actor(http), request.Input?.ToDomain() ?? throw StockReleaseInput.Invalid(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["expected"]["balanceRevision"].AsInt64);
            return TypedResults.Ok(StockReleaseResponses.Preview(row));
        }).WithName("StockReleasePreview");
        group.MapPost("/proposals", async (StockReleaseProposeDto request, HttpContext http,
            [FromServices] StockReleaseService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            Binding(request.Binding);
            var expected = request.Expected?.ToDomain() ?? throw StockReleaseInput.Invalid();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != expected.BalanceRevision) throw StockReleaseInput.Invalid();
            var row = await service.ProposeAsync(Actor(http), request.Input?.ToDomain() ?? throw StockReleaseInput.Invalid(),
                expected, request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockReleaseResponses.Command(row));
        }).WithName("ProposeStockRelease");
        group.MapPost("/proposals/{id}/review/query", async (string id, StockReleaseReviewQueryDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-review");
            var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, request.Decision(), request.Policy, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockReleaseResponses.Review(row));
        }).WithName("StockReleaseReview");
        foreach (var (suffix, decision, name) in new[] { ("approve", StockReleaseDecision.Approve, "ApproveStockRelease"),
            ("reject", StockReleaseDecision.Reject, "RejectStockRelease") })
            group.MapPost("/proposals/{id}/" + suffix, async (string id, StockReleaseDecideDto request, HttpContext http,
                [FromServices] StockReleaseService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
                Binding(request.Binding);
                var row = await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), decision,
                    request.Policy, request.Expected?.ToDomain() ?? throw StockReleaseInput.Invalid(), request.Binding,
                    new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                return TypedResults.Ok(StockReleaseResponses.Command(row));
            }).WithName(name);
        group.MapPost("/proposals/{id}/query", async (string id, StockReleaseReadDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "stock-release-history");
            var row = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            if (row is null) return Results.NotFound();
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return Results.Ok(StockReleaseResponses.Detail(row));
        }).WithName("StockReleaseDetail").Produces<StockReleaseDetailDto>(200);
        group.MapPost("/operations/query", async (StockReleaseReceiptQueryDto request, HttpContext http,
            [FromServices] StockReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery");
            var row = await service.RecoverAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new StockReleaseReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : StockReleaseResponses.Command(row)));
        }).WithName("StockReleaseReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string actual, string expected) {
        if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the stated stock-release purpose");
    }
    private static void Binding(string value) {
        if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigitLower(character))) throw StockReleaseInput.Invalid();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockReleaseInputDto([property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId QualityAllocationId,
    [property: JsonRequired] LongId ValuationAllocationId, [property: JsonRequired] int Packages, [property: JsonRequired] string Policy)
{
    internal StockReleaseInput ToDomain() {
        var value = new StockReleaseInput(LocationValue(Location), ReceiptId.Value, QualityAllocationId.Value, ValuationAllocationId.Value, Packages, Policy);
        value.Validate(); return value;
    }
    internal static StockLocation LocationValue(AccessScopeDto location) {
        var scope = StockCountInputs.Location(location);
        return new(new(scope.OrganizationId ?? throw StockReleaseInput.Invalid()), new(scope.ShopId ?? throw StockReleaseInput.Invalid()),
            new(scope.WarehouseId ?? throw StockReleaseInput.Invalid()), new(scope.ChannelId ?? throw StockReleaseInput.Invalid()));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockReleaseSourcesQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockReleaseHistoryQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    LongId? AfterProposalId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseExpectedDto([property: JsonRequired] LongId BalanceRevision, [property: JsonRequired] LongId PoolRevision,
    [property: JsonRequired] string PoolFence, [property: JsonRequired] string HeldValueRevision, [property: JsonRequired] string LotValueRevision)
{
    internal StockReleaseExpectation ToDomain() {
        var value = new StockReleaseExpectation(BalanceRevision.Value, PoolRevision.Value, Number(PoolFence), Number(HeldValueRevision), Number(LotValueRevision));
        value.Validate(); return value;
    }
    private static long Number(string value) => value == "0" ? 0L : value is null ? throw StockReleaseInput.Invalid() : LongId.Parse(value).Value;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleasePreviewQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] StockReleaseInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseProposeDto([property: JsonRequired] StockReleaseInputDto Input, [property: JsonRequired] StockReleaseExpectedDto Expected,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseReviewQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, [property: JsonRequired] string Policy)
{
    internal StockReleaseDecision Decision() => Action switch { "approve" => StockReleaseDecision.Approve,
        "reject" => StockReleaseDecision.Reject, _ => throw StockReleaseInput.Invalid() };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseDecideDto([property: JsonRequired] string Policy, [property: JsonRequired] StockReleaseExpectedDto Expected,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockReleaseReceiptQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockReleaseSelectionDto(LongId ReceiptId, LongId QualityAllocationId, LongId ValuationAllocationId,
    string AcceptedPackages, string ValuedPackages, string CapitalizedValue);
public sealed record StockReleaseStockDto(LongId BalanceId, LongId PoolId, LongId SkuId, LongId DefinitionId, LongId LotId,
    string Revision, string OnHandPackages, string ReservedPackages, string HeldPackages, string ExpiredPackages, string AvailablePackages);
public sealed record StockReleasePreviewDto(StockReleaseInputDto Input, StockReleaseExpectedDto Expected, StockReleaseSelectionDto Selection,
    StockReleaseStockDto StockBefore, StockReleaseStockDto StockAfter, string Binding, DateTimeOffset ObservedAt);
public sealed record StockReleaseCommandDto(LongId ProposalId, string Revision, string Status, LongId? DecisionId,
    LongId? MovementId, LongId? ValueTransitionId, string? TransferredValue);
public sealed record StockReleaseDecisionDto(LongId Id, string Action, DateTimeOffset DecidedAt, string Reason, LongId? MovementId,
    LongId? ValueTransitionId, string? TransferredValue);
public sealed record StockReleaseProposalDto(LongId Id, string Revision, StockReleaseInputDto Input, StockReleaseExpectedDto Expected,
    StockReleaseSelectionDto Selection, StockReleaseStockDto StockBefore, StockReleaseStockDto StockAfter, DateTimeOffset ProposedAt, string Reason);
public sealed record StockReleaseDetailDto(StockReleaseProposalDto Proposal, StockReleaseDecisionDto? Decision, string Status);
public sealed record StockReleaseReviewDto(StockReleaseProposalDto Proposal, StockReleaseExpectedDto Expected, StockReleaseSelectionDto Selection,
    StockReleaseStockDto StockBefore, StockReleaseStockDto StockAfter, string Policy, string Action, string Binding, DateTimeOffset ObservedAt);
public sealed record StockReleaseReceiptDto(string State, StockReleaseCommandDto? Result);
public sealed record StockReleaseSourceChoiceDto(LongId ReceiptId, LongId QualityAllocationId, LongId ValuationAllocationId,
    string ReceiptReference, string ProductName, string PackagingLabel, string LotReference, string? ExpiresOn,
    string AcceptedPackages, string ValuedPackages, string ReleaseablePackages, string CapitalizedValue, string Policy);
public sealed record StockReleaseSourcePageDto(StockReleaseSourceChoiceDto[] Items, string UnavailableCount);
public sealed record StockReleaseHistoryItemDto(LongId ProposalId, string Status, string ReceiptReference, string ProductName, string PackagingLabel,
    string LotReference, string Packages, string Policy, DateTimeOffset RecordedAt, DateTimeOffset? DecidedAt);
public sealed record StockReleaseHistoryPageDto(StockReleaseHistoryItemDto[] Items, LongId? NextAfter);

// The owning service authenticates private BSON. These projections name every public field;
// source, person, audit, invoice, journal and internal digest documents never cross this boundary.
internal static class StockReleaseResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static LongId? Id(BsonDocument row, string name) => row[name].IsBsonNull ? null : new(row[name].AsInt64);
    private static string? Text(BsonDocument row, string name) => row[name].IsBsonNull ? null : row[name].AsString;
    private static StockReleaseInputDto Input(BsonValue value) {
        var row = value.AsBsonDocument; var location = row["location"];
        return new(AccessScopeDto.From(new StockLocation(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)).ToScope()), new(row["receiptId"].AsInt64),
            new(row["qualityAllocationId"].AsInt64), new(row["valuationAllocationId"].AsInt64), checked((int)row["packages"].AsInt64), row["policy"].AsString);
    }
    private static StockReleaseExpectedDto Expected(BsonValue row) => new(new(row["balanceRevision"].AsInt64), new(row["poolRevision"].AsInt64),
        Number(row["poolFence"].AsInt64), Number(row["heldValueRevision"].AsInt64), Number(row["lotValueRevision"].AsInt64));
    private static StockReleaseSelectionDto Selection(BsonValue row) => new(new(row["receiptId"].AsInt64), new(row["qualityAllocationId"].AsInt64),
        new(row["valuationAllocationId"].AsInt64), Number(row["acceptedPackages"].ToInt64()), Number(row["valuedPackages"].ToInt64()), row["capitalizedValue"].AsString);
    private static StockReleaseStockDto Stock(BsonValue row) {
        var location = row["location"]; var stock = new StockBalance(new(new(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)), new(row["skuId"].AsInt64), new(row["definitionId"].AsInt64), new(row["lotId"].AsInt64)),
            row["onHand"].AsInt64, row["reserved"].AsInt64, row["held"].AsInt64, row["expired"].AsInt64, row["version"].AsInt64);
        return new(new(row["_id"].AsInt64), new(row["physicalPoolId"].AsInt64), new(row["skuId"].AsInt64), new(row["definitionId"].AsInt64), new(row["lotId"].AsInt64),
            Number(stock.Revision), Number(stock.OnHand), Number(stock.Reserved), Number(stock.Held), Number(stock.Expired), Number(StockBalancePolicy.AvailablePackages(stock)));
    }
    internal static StockReleasePreviewDto Preview(BsonDocument row) => new(Input(row["input"]), Expected(row["expected"]), Selection(row["source"]),
        Stock(row["before"]), Stock(row["after"]), row["binding"].AsString, Time(row["observedAt"]));
    internal static StockReleaseCommandDto Command(BsonDocument row) => new(new(row["proposalId"].AsInt64), Number(row["revision"].AsInt64), row["status"].AsString,
        Id(row, "decisionId"), Id(row, "movementId"), Id(row, "valueTransitionId"), Text(row, "transferredValue"));
    private static StockReleaseProposalDto Proposal(BsonValue value) {
        var row = value.AsBsonDocument;
        return new(new(row["_id"].AsInt64), "1", Input(row["input"]), Expected(row["expected"]), Selection(row["source"]), Stock(row["before"]), Stock(row["after"]),
            Time(row["recordedAt"]), row["reason"].AsString);
    }
    internal static StockReleaseReviewDto Review(BsonDocument row) {
        var expected = Expected(row["expected"]); var before = Stock(row["before"]); var after = Stock(row["after"]);
        if (before.Revision != expected.BalanceRevision.ToString()) throw new InvalidOperationException("Authenticated stock-release review revision is inconsistent");
        return new(Proposal(row["proposal"]), expected, Selection(row["source"]), before, after, row["policy"].AsString,
            row["decision"].AsString, row["binding"].AsString, Time(row["observedAt"]));
    }
    internal static StockReleaseDetailDto Detail(BsonDocument row) {
        var decision = row["decision"];
        return new(Proposal(row["proposal"]), decision.IsBsonNull ? null : new(new(decision["_id"].AsInt64), decision["decision"].AsString,
            Time(decision["recordedAt"]), decision["reason"].AsString, Id(decision.AsBsonDocument, "movementId"), Id(decision.AsBsonDocument, "valueTransitionId"),
            Text(decision.AsBsonDocument, "transferredValue")), decision.IsBsonNull ? "proposed" : decision["decision"] == "approve" ? "approved" : "rejected");
    }
    internal static StockReleaseSourcePageDto Sources(BsonDocument row) => new([.. row["items"].AsBsonArray.Select(value => {
        var item = value.AsBsonDocument;
        return new StockReleaseSourceChoiceDto(new(item["receiptId"].AsInt64), new(item["qualityAllocationId"].AsInt64), new(item["valuationAllocationId"].AsInt64),
            item["receiptReference"].AsString, item["productName"].AsString, item["packagingLabel"].AsString, item["lotReference"].AsString,
            item["expiresOn"].IsBsonNull ? null : item["expiresOn"].AsString, Number(item["acceptedPackages"].ToInt64()), Number(item["valuedPackages"].ToInt64()),
            Number(item["releaseablePackages"].ToInt64()), item["capitalizedValue"].AsString, item["policy"].AsString);
    })], Number(row["unavailableCount"].ToInt64()));
    internal static StockReleaseHistoryPageDto History(BsonDocument row) => new([.. row["items"].AsBsonArray.Select(value => {
        var item = value.AsBsonDocument;
        return new StockReleaseHistoryItemDto(new(item["proposalId"].AsInt64), item["status"].AsString, item["receiptReference"].AsString,
            item["productName"].AsString, item["packagingLabel"].AsString, item["lotReference"].AsString, Number(item["packages"].ToInt64()),
            item["policy"].AsString, Time(item["recordedAt"]), item.TryGetValue("decidedAt", out var decided) && !decided.IsBsonNull ? Time(decided) : null);
    })], row["nextAfter"].IsBsonNull || row["nextAfter"].ToInt64() == 0 ? null : new(row["nextAfter"].AsInt64));
}

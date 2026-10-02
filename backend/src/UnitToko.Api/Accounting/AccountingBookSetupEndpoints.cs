using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Accounting;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupKeyDto([property: JsonRequired] LongId OrganizationId,
    [property: JsonRequired] LongId ShopId, [property: JsonRequired] LongId ChannelId, [property: JsonRequired] string Currency) {
    internal OperationalBookKey Domain() => AccountingBookSetupRules.Key(new(OrganizationId.Value, ShopId.Value, ChannelId.Value, Currency));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupMappingDto([property: JsonRequired] string MappingKey, [property: JsonRequired] string AccountCode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupInputDto([property: JsonRequired] AccountingBookSetupKeyDto Key,
    [property: JsonRequired] string InitialMonth, [property: JsonRequired] AccountingBookSetupMappingDto[] Mappings) {
    internal AccountingBookSetupInput Domain() {
        if (Key is null || Mappings is not { Length: > 0 and <= 1000 } || Mappings.Any(value => value is null)) throw AccountingBookSetupRules.Invalid();
        return AccountingBookSetupRules.Copy(new(Key.Domain(), InitialMonth, [.. Mappings.Select(value => new AccountingBookMappingInput(value.MappingKey, value.AccountCode))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccountingBookSetupKeyDto Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupHistoryRequestDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccountingBookSetupKeyDto Key, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccountingBookSetupInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupProposeDto([property: JsonRequired] AccountingBookSetupInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupDecisionPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingBookSetupRecoveryRequestDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record AccountingBookSetupLabelDto(LongId Id, string Code, string Name, string ScopeVersion, string ProfileVersion);
public sealed record AccountingBookSetupChoiceDto(AccountingBookSetupKeyDto Key, AccountingBookSetupLabelDto Organization,
    AccountingBookSetupLabelDto Shop, AccountingBookSetupLabelDto Channel, bool Configured);
public sealed record AccountingBookSetupTargetsDto(AccountingBookSetupChoiceDto[] Items);
public sealed record AccountingBookSetupPreviewDto(AccountingBookSetupInputDto Input, string Binding, DateTimeOffset ObservedAt,
    AccountingBookSetupLabelDto Organization, AccountingBookSetupLabelDto Shop, AccountingBookSetupLabelDto Channel);
public sealed record AccountingBookSetupAccountDto(string MappingKey, LongId AccountId, string AccountCode, bool Active);
public sealed record AccountingBookSetupDecisionDto(LongId Id, string Decision, DateTimeOffset DecidedAt,
    LongId? BookId, LongId? ChartId, LongId? PeriodId, AccountingBookSetupAccountDto[] Accounts);
public sealed record AccountingBookSetupProposalDto(LongId Id, string Version, AccountingBookSetupKeyDto Key,
    AccountingBookSetupLabelDto Organization, AccountingBookSetupLabelDto Shop, AccountingBookSetupLabelDto Channel,
    string InitialMonth, AccountingBookSetupMappingDto[] Mappings, DateTimeOffset ProposedAt, string State, AccountingBookSetupDecisionDto? Decision);
public sealed record AccountingBookSetupCurrentDto(AccountingBookSetupKeyDto Key, bool Configured, AccountingBookSetupProposalDto? Setup);
public sealed record AccountingBookSetupHistoryDto(AccountingBookSetupKeyDto Key, AccountingBookSetupProposalDto[] Items, LongId? NextProposalId);
public sealed record AccountingBookSetupDecisionPreviewDto(AccountingBookSetupProposalDto Proposal, string Decision, string Binding, DateTimeOffset ObservedAt);
public sealed record AccountingBookSetupResultDto(LongId ProposalId, string ProposalVersion, LongId? DecisionId,
    LongId? BookId, LongId? ChartId, LongId? PeriodId, string Status);
public sealed record AccountingBookSetupRecoveryDto(string Status, AccountingBookSetupResultDto? Result);

public static class AccountingBookSetupEndpoints
{
    public static void MapAccountingBookSetupEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/accounting/books").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (AccountingBookSetupReadDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new AccountingBookSetupTargetsDto([.. row["items"].AsBsonArray.Select(Choice)]));
        }).WithName("AccountingBookSetupTargets");
        group.MapPost("/preview/query", async (AccountingBookSetupPreviewRequestDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw AccountingBookSetupRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new AccountingBookSetupPreviewDto(Input(row["input"]), row["binding"].AsString, Date(row["observedAt"]),
                Label(row["organization"]), Label(row["shop"]), Label(row["channel"])));
        }).WithName("AccountingBookSetupPreview");
        group.MapPost("/proposals", async (AccountingBookSetupProposeDto request, HttpContext http, [FromServices] AccountingBookSetupService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw AccountingBookSetupRules.Invalid();
            return TypedResults.Ok(Result(await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeAccountingBookSetup");
        group.MapPost("/current/query", async (AccountingBookSetupQueryDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.CurrentAsync(Actor(http), Key(request.Key), ct);
            return TypedResults.Ok(new AccountingBookSetupCurrentDto(Key(row["key"]), row["configured"].AsBoolean, row["setup"].IsBsonNull ? null : Proposal(row["setup"])));
        }).WithName("CurrentAccountingBookSetup");
        group.MapPost("/proposals/query", async (AccountingBookSetupHistoryRequestDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Key(request.Key), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new AccountingBookSetupHistoryDto(Key(row["key"]), [.. row["items"].AsBsonArray.Select(Proposal)], Id(row["nextProposalId"])));
        }).WithName("AccountingBookSetupHistory");
        group.MapPost("/proposals/{id}/query", async (string id, AccountingBookSetupReadDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Proposal(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("AccountingBookSetupProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, AccountingBookSetupDecisionPreviewRequestDto request, HttpContext http,
            [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new AccountingBookSetupDecisionPreviewDto(Proposal(row["proposal"]), row["decision"].AsString, row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("AccountingBookSetupDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, AccountingBookSetupDecideDto request, HttpContext http, [FromServices] AccountingBookSetupService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideAccountingBookSetup");
        group.MapPost("/operations/query", async (AccountingBookSetupRecoveryRequestDto request, HttpContext http, [FromServices] AccountingBookSetupService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw AccountingBookSetupRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new AccountingBookSetupRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverAccountingBookSetup");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "accounting-book-review") throw AccountingBookSetupRules.Invalid(); }
    private static AccountingBookSetupDecision Decision(string value) => value switch { "approve" => AccountingBookSetupDecision.Approve, "reject" => AccountingBookSetupDecision.Reject, _ => throw AccountingBookSetupRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static OperationalBookKey Key(AccountingBookSetupKeyDto value) => value is null ? throw AccountingBookSetupRules.Invalid() : value.Domain();
    private static AccountingBookSetupKeyDto Key(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64), new(value["channelId"].AsInt64), value["currency"].AsString);
    private static AccountingBookSetupLabelDto Label(BsonValue value) => new(new(value["id"].AsInt64), value["code"].AsString, value["name"].AsString, Number(value["scopeVersion"]), Number(value["profileVersion"]));
    private static AccountingBookSetupMappingDto[] Mappings(BsonValue value) => [.. value.AsBsonArray.Select(item => new AccountingBookSetupMappingDto(item["mappingKey"].AsString, item["accountCode"].AsString))];
    private static AccountingBookSetupInputDto Input(BsonValue value) => new(Key(value["key"]), value["initialMonth"].AsString, Mappings(value["mappings"]));
    private static AccountingBookSetupChoiceDto Choice(BsonValue value) => new(Key(value["key"]), Label(value["organization"]), Label(value["shop"]), Label(value["channel"]), value["configured"].AsBoolean);
    private static AccountingBookSetupDecisionDto DecisionView(BsonValue value) => new(new(value["id"].AsInt64), value["decision"].AsString, Date(value["decidedAt"]),
        Id(value["bookId"]), Id(value["chartId"]), Id(value["periodId"]), [.. value["accounts"].AsBsonArray.Select(item => new AccountingBookSetupAccountDto(item["mappingKey"].AsString, new(item["accountId"].AsInt64), item["accountCode"].AsString, item["active"].AsBoolean))]);
    private static AccountingBookSetupProposalDto Proposal(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), Key(value["key"]), Label(value["organization"]), Label(value["shop"]), Label(value["channel"]),
        value["initialMonth"].AsString, Mappings(value["mappings"]), Date(value["proposedAt"]), value["state"].AsString, value["decision"].IsBsonNull ? null : DecisionView(value["decision"]));
    private static AccountingBookSetupResultDto Result(BsonValue value) => new(new(value["proposalId"].AsInt64), Number(value["proposalVersion"]), Id(value["decisionId"]), Id(value["bookId"]), Id(value["chartId"]), Id(value["periodId"]), value["status"].AsString);
}

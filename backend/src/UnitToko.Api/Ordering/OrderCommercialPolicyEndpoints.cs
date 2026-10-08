using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Accounting;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Ordering;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyTargetDto([property: JsonRequired] ScopeNodeKind Kind, [property: JsonRequired] LongId Id,
    [property: JsonRequired] AccountingBookSetupKeyDto Seller);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialApplicabilityDto([property: JsonRequired] string Budget, [property: JsonRequired] string CostCenter,
    [property: JsonRequired] string Spending, [property: JsonRequired] string CreditHold) {
    private static OrderCommercialApplicability Value(string value) => value switch { "required" => OrderCommercialApplicability.Required,
        "not_applicable" => OrderCommercialApplicability.NotApplicable, _ => throw OrderCommercialPolicyRules.Invalid() };
    internal OrderCommercialApplicabilityRule Domain() => new(Value(Budget), Value(CostCenter), Value(Spending), Value(CreditHold));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialSellerVersionsDto([property: JsonRequired] string OrganizationScopeVersion, [property: JsonRequired] string OrganizationProfileVersion,
    [property: JsonRequired] string ShopScopeVersion, [property: JsonRequired] string ShopProfileVersion,
    [property: JsonRequired] string ChannelScopeVersion, [property: JsonRequired] string ChannelProfileVersion) {
    internal OrderCommercialSellerVersions Domain() => new(OrderCommercialPolicyEndpoints.Version(OrganizationScopeVersion), OrderCommercialPolicyEndpoints.Version(OrganizationProfileVersion),
        OrderCommercialPolicyEndpoints.Version(ShopScopeVersion), OrderCommercialPolicyEndpoints.Version(ShopProfileVersion),
        OrderCommercialPolicyEndpoints.Version(ChannelScopeVersion), OrderCommercialPolicyEndpoints.Version(ChannelProfileVersion));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OrderCommercialInvoiceTermsDto([property: JsonRequired] string Grouping, [property: JsonRequired] string TermOrigin,
    [property: JsonRequired] int TermDays, [property: JsonRequired] string DayRule, [property: JsonRequired] int[] WorkingDays,
    [property: JsonRequired] string[] Holidays, [property: JsonRequired] string NumberingProcedure,
    [property: JsonRequired] string ReissueProcedure, [property: JsonRequired] string TaxDocumentProcess) {
    internal OrderCommercialInvoiceTerms Domain() {
        if (WorkingDays is null || WorkingDays.Length > 7 || Holidays is null || Holidays.Length > 730) throw OrderCommercialPolicyRules.Invalid();
        DateOnly Holiday(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && value == day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ? day : throw OrderCommercialPolicyRules.Invalid();
        return new(Grouping, TermOrigin, TermDays, DayRule, [.. WorkingDays.Select(day => (DayOfWeek)day)], [.. Holidays.Select(Holiday)], NumberingProcedure, ReissueProcedure, TaxDocumentProcess);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialRuleDto([property: JsonRequired] OrderCommercialApplicabilityDto Applicability, [property: JsonRequired] Money Charges,
    [property: JsonRequired] string TaxTreatment, [property: JsonRequired] string TaxRate, [property: JsonRequired] string? ExemptionReference,
    [property: JsonRequired] string TaxBase, [property: JsonRequired] string TaxRounding, [property: JsonRequired] string Rounding,
    [property: JsonRequired] OrderCommercialInvoiceTermsDto Terms) {
    internal OrderCommercialRule Domain() => Applicability is null || Terms is null ? throw OrderCommercialPolicyRules.Invalid() :
        OrderCommercialPolicyRules.CopyRule(new(Applicability.Domain(), Charges, TaxTreatment, NumericContracts.ParseRate(TaxRate), ExemptionReference,
            TaxBase, TaxRounding, Rounding, Terms.Domain()));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyInputDto([property: JsonRequired] OrderCommercialPolicyTargetDto Target,
    [property: JsonRequired] string ExpectedScopeVersion, [property: JsonRequired] string ExpectedProfileVersion,
    [property: JsonRequired] OrderCommercialSellerVersionsDto SellerVersions,
    [property: JsonRequired] string ExpectedPolicyVersion, [property: JsonRequired] OrderCommercialRuleDto Rule,
    [property: JsonRequired] DateTimeOffset ValidFrom, [property: JsonRequired] DateTimeOffset ValidUntil) {
    internal OrderCommercialPolicyInput Domain() {
        if (Target is null || Target.Seller is null || SellerVersions is null || Rule is null) throw OrderCommercialPolicyRules.Invalid();
        return OrderCommercialPolicyRules.Copy(new(new(Target.Kind, Target.Id.Value, Target.Seller.Domain()), OrderCommercialPolicyEndpoints.Version(ExpectedScopeVersion),
            OrderCommercialPolicyEndpoints.Version(ExpectedProfileVersion), SellerVersions.Domain(), OrderCommercialPolicyEndpoints.Version(ExpectedPolicyVersion), Rule.Domain(), ValidFrom, ValidUntil));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyTargetsRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccountingBookSetupKeyDto Seller, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicySellersRequestDto([property: JsonRequired] string Purpose, LongId? BeforeShopId = null, LongId? BeforeChannelId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderCommercialPolicyTargetDto Target);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderCommercialPolicyInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyProposeDto([property: JsonRequired] OrderCommercialPolicyInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyHistoryRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderCommercialPolicyTargetDto Target, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyDecisionPreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCommercialPolicyRecoveryRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record OrderCommercialPolicyTargetChoiceDto(OrderCommercialPolicyTargetDto Target, OrderContextDto Context, string Code, string Name,
    OrderCommercialSellerLabelsDto Seller, OrderCommercialSellerVersionsDto SellerVersions,
    string ExpectedScopeVersion, string ExpectedProfileVersion, string PolicyVersion, LongId? PolicyId, LongId? VersionId);
public sealed record OrderCommercialPolicyTargetsDto(OrderCommercialPolicyTargetChoiceDto[] Items, LongId? NextTargetId);
public sealed record OrderCommercialSellerLabelsDto(string OrganizationName, string ShopName, string ChannelName);
public sealed record OrderCommercialSellerChoiceDto(AccountingBookSetupKeyDto Seller, string OrganizationName, string ShopName, string ChannelName, OrderCommercialSellerVersionsDto SellerVersions);
public sealed record OrderCommercialSellerCursorDto(LongId ShopId, LongId ChannelId);
public sealed record OrderCommercialPolicySellersDto(OrderCommercialSellerChoiceDto[] Items, OrderCommercialSellerCursorDto? NextSeller);
public sealed record OrderCommercialPolicyPreviewViewDto(OrderCommercialPolicyTargetDto Target, OrderContextDto Context, string Code, string Name,
    string ScopeVersion, string ProfileVersion, string PolicyVersion, OrderCommercialSellerVersionsDto SellerVersions, OrderCommercialSellerLabelsDto Seller,
    OrderCommercialRuleDto Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string SourceHash);
public sealed record OrderCommercialPolicyPreviewDto(string Binding, OrderCommercialPolicyPreviewViewDto View);
public sealed record OrderCommercialPolicyCurrentDto(OrderCommercialPolicyTargetDto Target, OrderContextDto Context, string Code, string Name,
    OrderCommercialSellerLabelsDto Seller, OrderCommercialSellerVersionsDto SellerVersions, string ExpectedScopeVersion, string ExpectedProfileVersion, LongId? PolicyId,
    string Version, LongId? VersionId, OrderCommercialRuleDto? Rule, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, DateTimeOffset? ApprovedAt, bool Current, bool Active);
public sealed record OrderCommercialPolicyProposalDto(LongId ProposalId, LongId PolicyId, string Revision, OrderCommercialPolicyTargetDto Target, OrderContextDto Context,
    string Code, string Name, OrderCommercialSellerLabelsDto Seller, OrderCommercialSellerVersionsDto SellerVersions,
    OrderCommercialRuleDto Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string BaseVersion,
    DateTimeOffset ProposedAt, string State, string? Decision, string? PolicyVersion, LongId? VersionId, bool BindingCurrent);
public sealed record OrderCommercialPolicyHistoryDto(OrderCommercialPolicyProposalDto[] Items, LongId? NextProposalId);
public sealed record OrderCommercialPolicyDecisionPreviewDto(string Binding, OrderCommercialPolicyProposalDto View);
public sealed record OrderCommercialPolicyResultDto(LongId PolicyId, LongId ProposalId, LongId RecordId, string Operation, string? PolicyVersion, LongId? VersionId, DateTimeOffset RecordedAt);
public sealed record OrderCommercialPolicyRecoveryDto(string Status, OrderCommercialPolicyResultDto? Result);

public static class OrderCommercialPolicyEndpoints
{
    internal static void ConfigureOpenApi(OpenApiDocument document) {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Commercial DTO schemas were not generated");
        void Set(string type, string property, OpenApiSchema schema) {
            if (schemas[type].Properties is not { } fields || !fields.ContainsKey(property)) throw new InvalidOperationException("Commercial DTO property was not generated");
            fields[property] = schema;
        }
        OpenApiSchema Choice(params string[] values) => new() { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
        OpenApiSchema Revision(bool zero = false) => new() { Type = JsonSchemaType.String, MaxLength = 19,
            Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])" };
        foreach (var type in new[] { nameof(OrderCommercialPolicySellersRequestDto), nameof(OrderCommercialPolicyTargetsRequestDto), nameof(OrderCommercialPolicyQueryDto),
            nameof(OrderCommercialPolicyPreviewRequestDto), nameof(OrderCommercialPolicyHistoryRequestDto), nameof(OrderCommercialPolicyReadDto), nameof(OrderCommercialPolicyDecisionPreviewRequestDto) })
            Set(type, "purpose", Choice("commercial-policy-review"));
        Set(nameof(OrderCommercialPolicyRecoveryRequestDto), "purpose", Choice("operation-recovery"));
        foreach (var field in new[] { "budget", "costCenter", "spending", "creditHold" }) Set(nameof(OrderCommercialApplicabilityDto), field, Choice("required", "not_applicable"));
        Set(nameof(OrderCommercialRuleDto), "taxRate", new() { Type = JsonSchemaType.String, MaxLength = 8,
            Pattern = @"^(?:(?:0|[1-9][0-9]?)(?:\.[0-9]{1,4})?|100(?:\.0{1,4})?)$(?![\s\S])" });
        Set(nameof(OrderCommercialPolicyTargetDto), "kind", Choice("branch", "division"));
        Set(nameof(OrderCommercialRuleDto), "taxTreatment", Choice("taxable_exclusive", "exempt"));
        Set(nameof(OrderCommercialRuleDto), "taxBase", Choice("subtotal", "subtotal_and_charges"));
        Set(nameof(OrderCommercialRuleDto), "taxRounding", Choice("line", "document")); Set(nameof(OrderCommercialRuleDto), "rounding", Choice("half_even", "half_up"));
        foreach (var field in new[] { "organizationScopeVersion", "organizationProfileVersion", "shopScopeVersion", "shopProfileVersion", "channelScopeVersion", "channelProfileVersion" })
            Set(nameof(OrderCommercialSellerVersionsDto), field, Revision());
        foreach (var field in new[] { "expectedScopeVersion", "expectedProfileVersion" }) Set(nameof(OrderCommercialPolicyInputDto), field, Revision());
        Set(nameof(OrderCommercialPolicyInputDto), "expectedPolicyVersion", Revision(true));
        Set(nameof(OrderCommercialInvoiceTermsDto), "grouping", Choice("receipt", "buying_branch_period_channel", "buying_org_period_channel"));
        Set(nameof(OrderCommercialInvoiceTermsDto), "termOrigin", Choice("issue_date", "earliest_receipt", "latest_receipt"));
        Set(nameof(OrderCommercialInvoiceTermsDto), "dayRule", Choice("calendar_days", "working_days"));
        Set(nameof(OrderCommercialInvoiceTermsDto), "termDays", new() { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = "365" });
        Set(nameof(OrderCommercialInvoiceTermsDto), "workingDays", new() { Type = JsonSchemaType.Array, MaxItems = 7, UniqueItems = true,
            Items = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = "6" } });
        Set(nameof(OrderCommercialInvoiceTermsDto), "holidays", new() { Type = JsonSchemaType.Array, MaxItems = 730, UniqueItems = true,
            Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date", Pattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}$(?![\s\S])" } });
        foreach (var type in new[] { nameof(OrderCommercialPolicyDecisionPreviewRequestDto), nameof(OrderCommercialPolicyDecideDto) }) Set(type, "decision", Choice("approve", "reject"));
        foreach (var type in new[] { nameof(OrderCommercialPolicyProposeDto), nameof(OrderCommercialPolicyDecideDto) }) {
            Set(type, "binding", new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = @"^[0-9a-f]{64}$(?![\s\S])" });
            Set(type, "reason", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1000 });
        }
        Set(nameof(OrderCommercialPolicyRecoveryRequestDto), "operation", Choice("ordering.commercial.policy.propose", "ordering.commercial.policy.approve", "ordering.commercial.policy.reject"));
        Set(nameof(OrderCommercialPolicyRecoveryDto), "status", Choice("not_confirmed", "confirmed"));
    }
    public static void MapOrderCommercialPolicyEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/commercial-policies").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store, max-age=0";
            context.HttpContext.Response.Headers.Pragma = "no-cache"; return await next(context);
        });
        group.AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/sellers/query", async (OrderCommercialPolicySellersRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.SellerChoicesAsync(Actor(http), request.BeforeShopId?.Value, request.BeforeChannelId?.Value, ct);
            var cursor = row["nextSeller"];
            return TypedResults.Ok(new OrderCommercialPolicySellersDto([.. row["items"].AsBsonArray.Select(item => new OrderCommercialSellerChoiceDto(Key(item["seller"]),
                item["organizationName"].AsString, item["shopName"].AsString, item["channelName"].AsString, SellerVersions(item["sellerVersions"])))],
                cursor.IsBsonNull ? null : new(new(cursor["shopId"].AsInt64), new(cursor["channelId"].AsInt64))));
        }).WithName("OrderCommercialPolicySellers");
        group.MapPost("/targets/query", async (OrderCommercialPolicyTargetsRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Seller is null) throw OrderCommercialPolicyRules.Invalid();
            var row = await service.TargetsAsync(Actor(http), request.Seller.Domain(), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrderCommercialPolicyTargetsDto([.. row["items"].AsBsonArray.Select(TargetChoice)], Id(row["nextTargetId"])));
        }).WithName("OrderCommercialPolicyTargets");
        group.MapPost("/preview/query", async (OrderCommercialPolicyPreviewRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw OrderCommercialPolicyRules.Invalid();
            var value = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(Version(request.Input.ExpectedPolicyVersion));
            return TypedResults.Ok(new OrderCommercialPolicyPreviewDto(value.Binding, Preview(value.View)));
        }).WithName("OrderCommercialPolicyPreview");
        group.MapPost("/proposals", async (OrderCommercialPolicyProposeDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw OrderCommercialPolicyRules.Invalid();
            var row = await service.ProposeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(Result(row));
        }).WithName("ProposeOrderCommercialPolicy");
        group.MapPost("/current/query", async (OrderCommercialPolicyQueryDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadAsync(Actor(http), Target(request.Target), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["version"].AsInt64);
            return TypedResults.Ok(new OrderCommercialPolicyCurrentDto(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
                SellerLabels(row["seller"]), SellerVersions(row["sellerVersions"]), Number(row["expectedScopeVersion"]), Number(row["expectedProfileVersion"]),
                Id(row["policyId"]), Number(row["version"]), Id(row["versionId"]), row["rule"].IsBsonNull ? null : Rule(row["rule"]),
                OptionalDate(row["validFrom"]), OptionalDate(row["validUntil"]), OptionalDate(row["approvedAt"]), row["current"].AsBoolean, row["active"].AsBoolean));
        }).WithName("CurrentOrderCommercialPolicy");
        group.MapPost("/proposals/query", async (OrderCommercialPolicyHistoryRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListProposalsAsync(Actor(http), Target(request.Target), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrderCommercialPolicyHistoryDto([.. row["items"].AsBsonArray.Select(Proposal)], Id(row["nextProposalId"])));
        }).WithName("OrderCommercialPolicyHistory");
        group.MapPost("/proposals/{id}/query", async (string id, OrderCommercialPolicyReadDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(Proposal(await service.ReadProposalAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("OrderCommercialPolicyProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, OrderCommercialPolicyDecisionPreviewRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, 1, Decision(request.Decision), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(new OrderCommercialPolicyDecisionPreviewDto(value.Binding, Proposal(value.View)));
        }).WithName("OrderCommercialPolicyDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, OrderCommercialPolicyDecideDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            var row = await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(Result(row));
        }).WithName("DecideOrderCommercialPolicy");
        group.MapPost("/operations/query", async (OrderCommercialPolicyRecoveryRequestDto request, HttpContext http, [FromServices] OrderCommercialPolicyService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw OrderCommercialPolicyRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new OrderCommercialPolicyRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverOrderCommercialPolicy");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string purpose) { if (purpose != "commercial-policy-review") throw OrderCommercialPolicyRules.Invalid(); }
    internal static long Version(string value) {
        try { return OrderSpendingPolicyEndpoints.Version(value); } catch (DomainValidationException) { throw OrderCommercialPolicyRules.Invalid(); }
    }
    private static OrderCommercialPolicyDecision Decision(string value) => value switch { "approve" => OrderCommercialPolicyDecision.Approve, "reject" => OrderCommercialPolicyDecision.Reject, _ => throw OrderCommercialPolicyRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static DateTimeOffset? OptionalDate(BsonValue value) => value.IsBsonNull ? null : Date(value);
    private static OrderCommercialPolicyTarget Target(OrderCommercialPolicyTargetDto value) => value?.Seller is null ? throw OrderCommercialPolicyRules.Invalid() : new(value.Kind, value.Id.Value, value.Seller.Domain());
    private static OrderCommercialPolicyTargetDto Target(BsonValue value) => new(Enum.Parse<ScopeNodeKind>(value["kind"].AsString, true), new(value["id"].AsInt64), Key(value["seller"]));
    private static AccountingBookSetupKeyDto Key(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64), new(value["channelId"].AsInt64), value["currency"].AsString);
    private static OrderCommercialSellerLabelsDto SellerLabels(BsonValue value) => new(value["organizationName"].AsString, value["shopName"].AsString, value["channelName"].AsString);
    private static OrderCommercialSellerVersionsDto SellerVersions(BsonValue value) => new(Number(value["organizationScopeVersion"]), Number(value["organizationProfileVersion"]),
        Number(value["shopScopeVersion"]), Number(value["shopProfileVersion"]), Number(value["channelScopeVersion"]), Number(value["channelProfileVersion"]));
    private static OrderContextDto Context(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["branchId"].AsInt64), Id(value["divisionId"]));
    private static OrderCommercialRuleDto Rule(BsonValue value) {
        var a = value["applicability"]; var t = value["terms"];
        return new(new(a["budget"].AsString, a["costCenter"].AsString, a["spending"].AsString, a["creditHold"].AsString),
            new Money(Decimal128.ToDecimal(value["charges"].AsDecimal128)), value["taxTreatment"].AsString,
            Decimal128.ToDecimal(value["taxRate"].AsDecimal128).ToString("0.####", CultureInfo.InvariantCulture), value["exemptionReference"].IsBsonNull ? null : value["exemptionReference"].AsString,
            value["taxBase"].AsString, value["taxRounding"].AsString, value["rounding"].AsString,
            new(t["grouping"].AsString, t["termOrigin"].AsString, t["termDays"].AsInt32, t["dayRule"].AsString, [.. t["workingDays"].AsBsonArray.Select(day => day.AsInt32)],
                [.. t["holidays"].AsBsonArray.Select(day => day.AsString)], t["numberingProcedure"].AsString, t["reissueProcedure"].AsString, t["taxDocumentProcess"].AsString));
    }
    private static OrderCommercialPolicyTargetChoiceDto TargetChoice(BsonValue row) => new(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
        SellerLabels(row["seller"]), SellerVersions(row["sellerVersions"]), Number(row["expectedScopeVersion"]), Number(row["expectedProfileVersion"]), Number(row["policyVersion"]), Id(row["policyId"]), Id(row["versionId"]));
    private static OrderCommercialPolicyPreviewViewDto Preview(BsonValue row) => new(Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString,
        Number(row["scopeVersion"]), Number(row["profileVersion"]), Number(row["policyVersion"]), SellerVersions(row["sellerVersions"]), SellerLabels(row["seller"]),
        Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]), row["sourceHash"].AsString);
    private static OrderCommercialPolicyProposalDto Proposal(BsonValue row) => new(new(row["proposalId"].AsInt64), new(row["policyId"].AsInt64), Number(row["revision"]),
        Target(row["target"]), Context(row["context"]), row["code"].AsString, row["name"].AsString, SellerLabels(row["seller"]), SellerVersions(row["sellerVersions"]),
        Rule(row["rule"]), Date(row["validFrom"]), Date(row["validUntil"]),
        Number(row["baseVersion"]), Date(row["proposedAt"]), row["state"].AsString, row["decision"].IsBsonNull ? null : row["decision"].AsString,
        row["policyVersion"].IsBsonNull ? null : Number(row["policyVersion"]), Id(row["versionId"]), row["bindingCurrent"].AsBoolean);
    private static OrderCommercialPolicyResultDto Result(BsonValue row) => new(new(row["policyId"].AsInt64), new(row["proposalId"].AsInt64), new(row["recordId"].AsInt64),
        row["operation"].AsString, row["policyVersion"].IsBsonNull ? null : Number(row["policyVersion"]), Id(row["versionId"]), Date(row["recordedAt"]));
}

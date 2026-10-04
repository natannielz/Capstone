using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId ReceiptId, [property: JsonRequired] string ReceiptRevision,
    [property: JsonRequired] LongId InvoiceDocumentId, [property: JsonRequired] string InvoiceDocumentRevision,
    [property: JsonRequired] LongId InvoiceMatchId, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int MatchLineNumber,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Packages, [property: JsonRequired] Money NetAmount, [property: JsonRequired] Money TaxAmount,
    [property: JsonRequired] Money ChargeAmount, [property: JsonRequired] Money CapitalizedAmount,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] string PostingDate, [property: JsonRequired] string Reason) {
    internal ProcurementStockValuationInput Domain() => ProcurementStockValuationRules.Copy(new(Location.Domain(), ReceiptId.Value,
        LongId.Parse(ReceiptRevision).Value, InvoiceDocumentId.Value, LongId.Parse(InvoiceDocumentRevision).Value,
        InvoiceMatchId.Value, MatchLineNumber, Packages, NetAmount, TaxAmount, ChargeAmount, CapitalizedAmount,
        PolicyId.Value, LongId.Parse(PolicyRevision).Value, DateOnly.ParseExact(PostingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), Reason));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationListDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationReceiptQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId ReceiptId, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationMatchesQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId ReceiptId, LongId? BeforeMatchId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementStockValuationInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationProposeDto([property: JsonRequired] ProcurementStockValuationInputDto Input, [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationDecisionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationDecideDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockValuationCostQueryDto([property: JsonRequired] string Purpose, LongId? BeforeAllocationId = null);

public sealed record ProcurementStockValuationCapacityDto(string Version, long AllocatedPackages, long RemainingPackages,
    LongId? LastAllocationId, string? LastHash);
public sealed record ProcurementStockValuationMatchCapacityDto(string Version, long AllocatedPackages, long RemainingPackages,
    Money AllocatedNetAmount, Money AllocatedTaxAmount, Money AllocatedChargeAmount, Money RemainingNetAmount,
    Money RemainingTaxAmount, Money RemainingChargeAmount, LongId? LastAllocationId, string? LastHash);
public sealed record ProcurementStockValuationReceiptChoiceDto(ProcurementStockReceiptDto Receipt, ProcurementStockValuationCapacityDto ReceiptCapacity);
public sealed record ProcurementStockValuationReceiptsDto(ProcurementLocationDto Location, ProcurementStockValuationReceiptChoiceDto[] Items, LongId? NextReceiptId);
public sealed record ProcurementStockValuationMatchLineDto(int DocumentLineNumber, int Packages, Money NetAmount, Money TaxAmount,
    Money ChargeAmount, LongId InspectionId, int InspectionLineNumber, string State);
public sealed record ProcurementStockValuationMatchChoiceDto(LongId InvoiceDocumentId, string InvoiceDocumentRevision, LongId InvoiceMatchId,
    int MatchLineNumber, string DocumentReference, ProcurementStockValuationMatchLineDto Line, ProcurementStockValuationMatchCapacityDto MatchCapacity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SupplierDocumentQualityDto? Quality = null);
public sealed record ProcurementStockValuationMatchesDto(LongId ReceiptId, ProcurementStockValuationMatchChoiceDto[] Items, LongId? NextMatchId);
public sealed record ProcurementStockValuationAccountDto(string MappingKey, LongId AccountId, string AccountCode);
public sealed record ProcurementStockValuationPolicyDto(LongId Id, string Revision, bool Active, ProcurementLocationDto Location, string Currency,
    DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, string SourceRecognition, LongId BookId, LongId ChartVersionId,
    ProcurementStockValuationAccountDto Inventory, ProcurementStockValuationAccountDto ReceiptAccrual, bool CapitalizeTax,
    bool CapitalizeCharge, string OperatingOwnerReference, string ApprovalReference);
public sealed record ProcurementStockValuationPoliciesDto(LongId ReceiptId, ProcurementStockValuationPolicyDto[] Items, LongId? NextPolicyId);
public sealed record ProcurementStockValuationAccountingDto(LongId BookId, LongId ChartVersionId, LongId PeriodId, string PeriodMonth,
    ProcurementStockValuationAccountDto Inventory, ProcurementStockValuationAccountDto ReceiptAccrual);
public sealed record ProcurementStockValuationPreviewDto(ProcurementStockValuationInputDto Input, ProcurementStockReceiptDto Receipt,
    ProcurementStockValuationMatchChoiceDto Match, ProcurementStockValuationPolicyDto Policy, ProcurementStockValuationAccountingDto Accounting,
    ProcurementStockValuationCapacityDto ReceiptCapacity, ProcurementStockValuationMatchCapacityDto MatchCapacity, string Binding, DateTimeOffset ObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcurementQualityAcceptanceCapacityDto? QualityCapacity = null);
public sealed record ProcurementStockValuationResultDto(LongId ProposalId, LongId? DecisionId, string Version, string Status,
    LongId? AllocationId, LongId? JournalId, DateTimeOffset RecordedAt);
public sealed record ProcurementStockValuationProposalDto(LongId Id, string Version, ProcurementStockValuationInputDto Input,
    ProcurementStockReceiptDto Receipt, ProcurementStockValuationMatchChoiceDto Match, ProcurementStockValuationPolicyDto Policy,
    ProcurementStockValuationAccountingDto Accounting, DateTimeOffset RecordedAt, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcurementQualityAcceptanceCapacityDto? QualityCapacity = null);
public sealed record ProcurementStockValuationReadDto(ProcurementStockValuationProposalDto Proposal,
    ProcurementStockValuationResultDto? Decision, string? DecisionReason);
public sealed record ProcurementStockValuationDecisionReviewDto(ProcurementStockValuationProposalDto Proposal, string Decision,
    ProcurementStockValuationCapacityDto ReceiptCapacity, ProcurementStockValuationMatchCapacityDto MatchCapacity,
    ProcurementStockValuationPolicyDto Policy, ProcurementStockValuationAccountingDto Accounting, string Binding, DateTimeOffset ObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcurementQualityAcceptanceCapacityDto? QualityCapacity = null);
public sealed record ProcurementStockValuationHistoryDto(ProcurementLocationDto Location, ProcurementStockValuationReadDto[] Items, LongId? NextProposalId);
public sealed record ProcurementStockValuationRecoveryDto(string Status, ProcurementStockValuationResultDto? Result);
public sealed record ProcurementStockValuationCostAllocationDto(LongId Id, LongId ProposalId, LongId DecisionId,
    LongId InvoiceDocumentId, LongId InvoiceMatchId, int MatchLineNumber, LongId JournalId, int Packages,
    Money NetAmount, Money TaxAmount, Money ChargeAmount, Money CapitalizedAmount, string PostingDate, DateTimeOffset RecordedAt);
public sealed record ProcurementStockValuationCostDto(LongId ReceiptId, LongId LotId, LongId BalanceId,
    long PackageCount, long ValuedPackages, long UnvaluedPackages, string Currency, string State,
    Money NetAmount, Money TaxAmount, Money ChargeAmount, Money CapitalizedAmount,
    ProcurementStockValuationCostAllocationDto[] Items, LongId? NextAllocationId);

public static class ProcurementStockValuationEndpoints
{
    public static void MapProcurementStockValuationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/stock-valuations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementStockValuationQueryDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementStockValuationTargets");
        group.MapPost("/receipts/query", async (ProcurementStockValuationListDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReceiptsAsync(Actor(http), request.Location.Domain(), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockValuationReceiptsDto(R.Location(row["location"]),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementStockValuationReceiptChoiceDto(Receipt(item["receipt"]), ReceiptCapacity(item["receiptCapacity"])))], Id(row["nextReceiptId"])));
        }).WithName("ProcurementStockValuationReceipts");
        group.MapPost("/receipts/{id}/cost/query", async (string id, ProcurementStockValuationCostQueryDto request, HttpContext http,
            [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.CostAsync(Actor(http), LongId.Parse(id).Value, request.BeforeAllocationId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockValuationCostDto(new(row["receiptId"].AsInt64), new(row["lotId"].AsInt64),
                new(row["balanceId"].AsInt64), row["packageCount"].AsInt64, row["valuedPackages"].AsInt64,
                row["unvaluedPackages"].AsInt64, row["currency"].AsString, row["state"].AsString,
                Money.Parse(row["netAmount"].AsString), Money.Parse(row["taxAmount"].AsString),
                Money.Parse(row["chargeAmount"].AsString), Money.Parse(row["capitalizedAmount"].AsString),
                [.. row["items"].AsBsonArray.Select(CostAllocation)], Id(row["nextAllocationId"])));
        }).WithName("ProcurementStockValuationReceiptCost");
        group.MapPost("/matches/query", async (ProcurementStockValuationMatchesQueryDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.MatchesAsync(Actor(http), request.ReceiptId.Value, request.BeforeMatchId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockValuationMatchesDto(new(row["receiptId"].AsInt64), [.. row["items"].AsBsonArray.Select(Match)], Id(row["nextMatchId"])));
        }).WithName("ProcurementStockValuationMatches");
        group.MapPost("/policies/query", async (ProcurementStockValuationReceiptQueryDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PoliciesAsync(Actor(http), request.ReceiptId.Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockValuationPoliciesDto(new(row["receiptId"].AsInt64), [.. row["items"].AsBsonArray.Select(Policy)], Id(row["nextPolicyId"])));
        }).WithName("ProcurementStockValuationPolicies");
        group.MapPost("/preview/query", async (ProcurementStockValuationPreviewInputDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementStockValuationPreviewDto(Input(row["input"]), Receipt(row["receipt"]), Match(row["match"]),
                Policy(row["policy"]), Accounting(row["accounting"]), ReceiptCapacity(row["receiptCapacity"]), MatchCapacity(row["matchCapacity"]),
                row["binding"].AsString, Date(row["observedAt"]), QualityCapacity(row)));
        }).WithName("PreviewProcurementStockValuation");
        group.MapPost("/proposals", async (ProcurementStockValuationProposeDto request, HttpContext http, [FromServices] ProcurementStockValuationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            var input = request.Input.Domain(); var row = await service.ProposeAsync(Actor(http), input, request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), input.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(row));
        }).WithName("ProposeProcurementStockValuation");
        group.MapPost("/proposals/{id}/query", async (string id, ProcurementStockValuationQueryDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementStockValuationProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, ProcurementStockValuationDecisionPreviewDto request, HttpContext http,
            [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw ProcurementStockValuationRules.Invalid();
            var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new ProcurementStockValuationDecisionReviewDto(Proposal(row["proposal"]), row["decision"].AsString,
                ReceiptCapacity(row["receiptCapacity"]), MatchCapacity(row["matchCapacity"]), Policy(row["policy"]), Accounting(row["accounting"]),
                row["binding"].AsString, Date(row["observedAt"]), QualityCapacity(row)));
        }).WithName("PreviewProcurementStockValuationDecision");
        group.MapPost("/proposals/{id}/decisions", async (string id, ProcurementStockValuationDecideDto request, HttpContext http,
            [FromServices] ProcurementStockValuationService service, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            var row = await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Result(row));
        }).WithName("DecideProcurementStockValuation");
        group.MapPost("/history/query", async (ProcurementStockValuationListDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.HistoryAsync(Actor(http), request.Location.Domain(), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockValuationHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Read)], Id(row["nextProposalId"])));
        }).WithName("ProcurementStockValuationHistory");
        group.MapPost("/operations/query", async (ProcurementStockValuationRecoveryInputDto request, HttpContext http, [FromServices] ProcurementStockValuationService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementStockValuationRecoveryDto(row is null ? "not_found" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementStockValuation");
    }
    private static ActorContext Actor(HttpContext http) { UnitToko.Api.Billing.PaymentAllocationEndpoints.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "procurement-stock-valuation-review") { if (value != expected) throw ProcurementStockValuationRules.Invalid(); }
    private static ProcurementStockValuationDecision Decision(string value) => value switch { "approve" => ProcurementStockValuationDecision.Approve,
        "reject" => ProcurementStockValuationDecision.Reject, _ => throw ProcurementStockValuationRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static ProcurementStockValuationInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["receiptId"].AsInt64),
        Number(row["receiptRevision"]), new(row["invoiceDocumentId"].AsInt64), Number(row["invoiceDocumentRevision"]),
        new(row["invoiceMatchId"].AsInt64), row["matchLineNumber"].AsInt32, row["packages"].AsInt32,
        Money.Parse(row["netAmount"].AsString), Money.Parse(row["taxAmount"].AsString), Money.Parse(row["chargeAmount"].AsString),
        Money.Parse(row["capitalizedAmount"].AsString), new(row["policyId"].AsInt64), Number(row["policyRevision"]),
        row["postingDate"].AsString, row["reason"].AsString);
    private static ProcurementStockValuationCapacityDto ReceiptCapacity(BsonValue row) => new(Number(row["version"]), row["allocatedPackages"].AsInt64,
        row["remainingPackages"].AsInt64, Id(row["lastAllocationId"]), row["lastHash"].IsBsonNull ? null : row["lastHash"].AsString);
    private static ProcurementStockValuationMatchCapacityDto MatchCapacity(BsonValue row) => new(Number(row["version"]), row["allocatedPackages"].AsInt64,
        row["remainingPackages"].AsInt64, Money.Parse(row["allocatedNetAmount"].AsString), Money.Parse(row["allocatedTaxAmount"].AsString),
        Money.Parse(row["allocatedChargeAmount"].AsString), Money.Parse(row["remainingNetAmount"].AsString), Money.Parse(row["remainingTaxAmount"].AsString),
        Money.Parse(row["remainingChargeAmount"].AsString), Id(row["lastAllocationId"]), row["lastHash"].IsBsonNull ? null : row["lastHash"].AsString);
    private static ProcurementStockValuationMatchChoiceDto Match(BsonValue row) => new(new(row["invoiceDocumentId"].AsInt64), Number(row["invoiceDocumentRevision"]),
        new(row["invoiceMatchId"].AsInt64), row["matchLineNumber"].AsInt32, row["documentReference"].AsString,
        new(row["line"]["documentLineNumber"].AsInt32, row["line"]["packages"].AsInt32, Money.Parse(row["line"]["netAmount"].AsString),
            Money.Parse(row["line"]["taxAmount"].AsString), Money.Parse(row["line"]["chargeAmount"].AsString),
            new(row["line"]["inspectionId"].AsInt64), row["line"]["inspectionLineNumber"].AsInt32, row["line"]["state"].AsString),
        MatchCapacity(row["matchCapacity"]), SupplierDocumentEndpoints.Quality(row.AsBsonDocument.GetValue("quality", BsonNull.Value)));
    private static ProcurementQualityAcceptanceCapacityDto? QualityCapacity(BsonValue row) =>
        row.AsBsonDocument.TryGetValue("qualityCapacity", out var capacity) && !capacity.IsBsonNull
            ? ProcurementQualityAcceptanceDtos.Capacity(capacity) : null;
    private static ProcurementStockValuationAccountDto Account(BsonValue row) => new(row["mappingKey"].AsString,
        new(row["accountId"].AsInt64), row["accountCode"].AsString);
    private static ProcurementStockValuationPolicyDto Policy(BsonValue row) => new(new(row["id"].AsInt64), Number(row["revision"]),
        row["active"].AsBoolean, R.Location(row["location"]), row["currency"].AsString, Date(row["approvedAt"]), Date(row["expiresAt"]),
        row["sourceRecognition"].AsString, new(row["bookId"].AsInt64), new(row["chartVersionId"].AsInt64),
        Account(row["inventory"]), Account(row["receiptAccrual"]), row["capitalizeTax"].AsBoolean, row["capitalizeCharge"].AsBoolean,
        row["operatingOwnerReference"].AsString, row["approvalReference"].AsString);
    private static ProcurementStockValuationAccountingDto Accounting(BsonValue row) => new(new(row["bookId"].AsInt64),
        new(row["chartVersionId"].AsInt64), new(row["periodId"].AsInt64), row["periodMonth"].AsString,
        Account(row["inventory"]), Account(row["receiptAccrual"]));
    private static ProcurementStockValuationResultDto Result(BsonValue row) => new(new(row["proposalId"].AsInt64),
        row.AsBsonDocument.Contains("decisionId") ? new(row["decisionId"].AsInt64) : null, Number(row["version"]), row["status"].AsString,
        row.AsBsonDocument.Contains("allocationId") ? Id(row["allocationId"]) : null, row.AsBsonDocument.Contains("journalId") ? Id(row["journalId"]) : null, Date(row["recordedAt"]));
    private static ProcurementStockValuationCostAllocationDto CostAllocation(BsonValue row) => new(new(row["id"].AsInt64),
        new(row["proposalId"].AsInt64), new(row["decisionId"].AsInt64), new(row["invoiceDocumentId"].AsInt64),
        new(row["invoiceMatchId"].AsInt64), row["matchLineNumber"].AsInt32, new(row["journalId"].AsInt64),
        row["packages"].AsInt32, Money.Parse(row["netAmount"].AsString), Money.Parse(row["taxAmount"].AsString),
        Money.Parse(row["chargeAmount"].AsString), Money.Parse(row["capitalizedAmount"].AsString),
        row["postingDate"].AsString, Date(row["recordedAt"]));
    private static ProcurementStockValuationProposalDto Proposal(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), Input(row["input"]),
        Receipt(row["receipt"]), Match(row["match"]), Policy(row["policy"]), Accounting(row["accounting"]),
        Date(row["recordedAt"]), row["reason"].AsString, QualityCapacity(row));
    private static ProcurementStockValuationReadDto Read(BsonValue row) {
        return new(Proposal(row["proposal"]), row["decision"].IsBsonNull ? null : Result(row["decision"]),
            row["decisionReason"].IsBsonNull ? null : row["decisionReason"].AsString);
    }
    private static ProcurementStockReceiptDto Receipt(BsonValue row) {
        var input = row["input"]; var source = row["source"]; var inspection = source["inspection"]; var pool = row["pool"];
        var original = new ProcurementStockReceiptInputDto(R.Location(input["location"]), new(input["orderId"].AsInt64), Number(input["orderVersion"]),
            new(input["orderApprovalId"].AsInt64), new(input["inspectionId"].AsInt64), input["inspectionLineNumber"].AsInt32,
            input["packages"].AsInt32, new(input["poolId"].AsInt64), Number(input["poolRevision"]), input["receiptReference"].AsString,
            input["lotReference"].AsString, input["expiresOn"].IsBsonNull ? null : input["expiresOn"].AsString, input["reason"].AsString);
        var inspectionDto = new ProcurementStockReceiptInspectionDto(new(inspection["id"].AsInt64), Number(inspection["version"]), inspection["hash"].AsString,
            inspection["inspectionDate"].AsString, inspection["inspectionReference"].AsString, new(inspection["noticeId"].AsInt64),
            new(inspection["intakeId"].AsInt64), inspection["lineNumber"].AsInt32, SupplierDocumentEndpoints.InspectionLine(inspection["line"]));
        var sourceDto = new ProcurementStockReceiptSourceDto(ProcurementFundingEndpoints.Order(source["order"]), inspectionDto);
        var poolDto = new ProcurementStockReceiptPoolDto(new(pool["id"].AsInt64), Number(pool["revision"]), pool["active"].AsBoolean,
            R.Location(pool["location"]), R.Location(pool["requestedLocation"]), pool["physicalKey"].AsString, pool["label"].AsString,
            pool["mode"].AsString, pool["control"].AsString, pool["configurationHash"].AsString, new(pool["policyId"].AsInt64),
            Number(pool["policyRevision"]), pool["policyHash"].AsString, pool["writer"].AsString,
            pool["reconciliationProcedureReference"].AsString, pool["outageProcedureReference"].AsString);
        SupplierClaimCapacityDto Physical(BsonValue value) => new(value["basis"].AsString, new(value["inspectionId"].AsInt64),
            value["inspectionLineNumber"].AsInt32, value["inspectionHash"].AsString, Id(value["headId"]), Number(value["version"]),
            Number(value["totalPackages"]), Number(value["occupiedPackages"]), Number(value["remainingPackages"]));
        return new(new(row["id"].AsInt64), Number(row["revision"]), original, sourceDto, poolDto, Physical(row["before"]), Physical(row["after"]),
            new(row["lotId"].AsInt64), new(row["balanceId"].AsInt64), new(row["movementId"].AsInt64), Number(row["onHand"]),
            Number(row["held"]), Number(row["reserved"]), Number(row["expired"]), Number(row["available"]), row["valuationState"].AsString,
            row["valuation"].IsBsonNull ? null : Money.Parse(row["valuation"].AsString), row["state"].AsString,
            new(row["recordedBy"].AsInt64), Date(row["recordedAt"]));
    }
}

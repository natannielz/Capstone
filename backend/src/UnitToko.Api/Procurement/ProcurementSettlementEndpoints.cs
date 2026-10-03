using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Billing;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementInputDto([property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] LongId BankImportId, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int BankRowNumber, [property: JsonRequired] ReceivingAccountDetailsDto ObservedBeneficiary,
    [property: JsonRequired] string BeneficiaryVerificationReference, [property: JsonRequired] string TransferVerificationReference) {
    internal ProcurementSettlementInput Domain() => ObservedBeneficiary is null ? throw ProcurementSettlementRules.Invalid() : ProcurementSettlementRules.Copy(new(
        PolicyId.Value, LongId.Parse(PolicyRevision).Value, BankImportId.Value, BankRowNumber, ObservedBeneficiary.Domain(), BeneficiaryVerificationReference, TransferVerificationReference));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementReadInputDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementImportsInputDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementSettlementInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementVerifyDto([property: JsonRequired] ProcurementSettlementInputDto Input, [property: JsonRequired] string Binding,
    [property: JsonRequired] bool Attested, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSettlementRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ProcurementSettlementOriginalDto(LongId DraftId, string DraftRevision, LongId ReservationId, PaymentEvidenceReferenceDto Reference);
public sealed record ProcurementSettlementDisbursementDto(LongId Id, string Version, LongId DraftId, ProcurementLocationDto Location, ProcurementDisbursementAuthorizationDto Authorization,
    Money Amount, string TransferDate, string TransferReference, DateTimeOffset RecordedAt, ProcurementDisbursementBeneficiaryDto Beneficiary,
    ProcurementDisbursementAccountDto PayingAccount, ProcurementSettlementOriginalDto Original);
public sealed record ProcurementSettlementSummaryDto(LongId Id, LongId DisbursementId, Money Amount, string Status, DateTimeOffset RecordedAt, string PostingDate, LongId JournalId);
public sealed record ProcurementSettlementDisbursementChoiceDto(ProcurementSettlementDisbursementDto Disbursement, ProcurementSettlementSummaryDto? Settlement);
public sealed record ProcurementSettlementDisbursementsDto(ProcurementLocationDto Location, ProcurementSettlementDisbursementChoiceDto[] Items, LongId? NextDisbursementId);
public sealed record ProcurementSettlementPolicyDto(LongId Id, ProcurementLocationDto Location, string Revision, bool Active, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt,
    string Rule, bool AllowReferenceOnly, LongId PayingAccountId, LongId PayingAccountVersionId, string Currency, LongId BookId, LongId ChartVersionId,
    string SupplierAdvanceMapping, LongId SupplierAdvanceAccountId, string SupplierAdvanceAccountCode, string CashMapping, LongId CashAccountId, string CashAccountCode,
    string BankDebitVerificationProcedureReference, string BeneficiaryVerificationProcedureReference, string TrustedBankChannelReference, string OperatingOwnerReference, string ApprovalReference);
public sealed record ProcurementSettlementOptionsDto(ProcurementSettlementDisbursementDto Disbursement, ProcurementSettlementPolicyDto[] Items);
public sealed record ProcurementSettlementImportDto(LongId Id, string Filename, string ProfileName, DateTimeOffset RecordedAt, int RowCount);
public sealed record ProcurementSettlementImportsDto(LongId DisbursementId, LongId PayingAccountId, LongId PayingAccountVersionId, ProcurementSettlementImportDto[] Items, LongId? NextImportId);
public sealed record ProcurementSettlementDebitChoiceDto(int RowNumber, string? TransactionId, string BookedDate, Money Amount, string Direction, string? Reference, bool Eligible, string? Issue);
public sealed record ProcurementSettlementDebitsDto(LongId DisbursementId, LongId ImportId, LongId PayingAccountId, LongId PayingAccountVersionId, ProcurementSettlementDebitChoiceDto[] Items);
public sealed record ProcurementSettlementDebitDto(LongId ImportId, int RowNumber, string TransactionId, string BookedDate, Money Amount, string Direction, string? Reference);
public sealed record ProcurementSettlementAccountDto(string MappingKey, LongId AccountId, string AccountCode);
public sealed record ProcurementSettlementAccountingDto(LongId BookId, LongId ChartVersionId, LongId PeriodId, string PeriodMonth, ProcurementSettlementAccountDto SupplierAdvance, ProcurementSettlementAccountDto Cash);
public sealed record ProcurementSettlementPreviewDto(ProcurementSettlementDisbursementDto Disbursement, ProcurementSettlementInputDto Input, ProcurementSettlementPolicyDto Policy,
    ProcurementSettlementDebitDto Debit, ProcurementSettlementAccountingDto Accounting, string PostingDate, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementSettlementViewDto(LongId Id, LongId DisbursementId, Money Amount, string Status, DateTimeOffset RecordedAt, string PostingDate, LongId JournalId,
    ProcurementSettlementDebitDto Debit, ProcurementSettlementPolicyDto Policy, ProcurementSettlementAccountingDto Accounting, string BeneficiaryVerificationReference, string TransferVerificationReference, string Reason);
public sealed record ProcurementSettlementReadDto(ProcurementSettlementDisbursementDto Disbursement, ProcurementSettlementViewDto? Settlement);
public sealed record ProcurementSettlementHistoryDto(ProcurementLocationDto Location, ProcurementSettlementViewDto[] Items, LongId? NextSettlementId);
public sealed record ProcurementSettlementRecoveryDto(string Status, ProcurementSettlementSummaryDto? Result);

public static class ProcurementSettlementEndpoints
{
    public static void MapProcurementSettlementEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/settlements").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementSettlementReadInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct); return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementSettlementTargets");
        group.MapPost("/disbursements/query", async (ProcurementSettlementListInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.DisbursementsAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementSettlementDisbursementsDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(item => new ProcurementSettlementDisbursementChoiceDto(Disbursement(item["disbursement"]), item["settlement"].IsBsonNull ? null : Summary(item["settlement"])))], Id(row["nextDisbursementId"])));
        }).WithName("ProcurementSettlementDisbursements");
        group.MapPost("/history/query", async (ProcurementSettlementListInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementSettlementHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(View)], Id(row["nextSettlementId"])));
        }).WithName("ProcurementSettlementHistory");
        group.MapPost("/operations/query", async (ProcurementSettlementRecoveryInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementSettlementRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Summary(row)));
        }).WithName("RecoverProcurementSettlement");
        var records = group.MapGroup("/disbursements/{id}");
        records.MapPost("/query", async (string id, ProcurementSettlementReadInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            return TypedResults.Ok(new ProcurementSettlementReadDto(Disbursement(row["disbursement"]), row["settlement"].IsBsonNull ? null : View(row["settlement"])));
        }).WithName("ReadProcurementSettlement");
        records.MapPost("/options/query", async (string id, ProcurementSettlementReadInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OptionsAsync(Actor(http), LongId.Parse(id).Value, ct);
            return TypedResults.Ok(new ProcurementSettlementOptionsDto(Disbursement(row["disbursement"]), [.. row["items"].AsBsonArray.Select(Policy)]));
        }).WithName("ProcurementSettlementOptions");
        records.MapPost("/bank-imports/query", async (string id, ProcurementSettlementImportsInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.BankImportsAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementSettlementImportsDto(new(row["disbursementId"].AsInt64), new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementSettlementImportDto(new(item["id"].AsInt64), item["filename"].AsString, item["profileName"].AsString, Date(item["recordedAt"]), item["rowCount"].AsInt32))], Id(row["nextImportId"])));
        }).WithName("ProcurementSettlementBankImports");
        records.MapPost("/bank-imports/{importId}/debits/query", async (string id, string importId, ProcurementSettlementReadInputDto request, HttpContext http, ProcurementSettlementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.DebitsAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(importId).Value, ct);
            return TypedResults.Ok(new ProcurementSettlementDebitsDto(new(row["disbursementId"].AsInt64), new(row["importId"].AsInt64), new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementSettlementDebitChoiceDto(item["rowNumber"].AsInt32, Text(item["transactionId"]), item["bookedDate"].AsString,
                    Amount(item["amount"]), item["direction"].AsString, Text(item["reference"]), item["eligible"].AsBoolean, Text(item["issue"]))) ]));
        }).WithName("ProcurementSettlementDebits");
        records.MapPost("/preview/query", async (string id, ProcurementSettlementPreviewInputDto request, HttpContext http, ProcurementSettlementService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementSettlementRules.Invalid();
            return TypedResults.Ok(Preview(await service.PreviewAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), ct)));
        }).WithName("PreviewProcurementSettlement");
        records.MapPost("/verify", async (string id, ProcurementSettlementVerifyDto request, HttpContext http, ProcurementSettlementService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementSettlementRules.Invalid();
            return TypedResults.Ok(Summary(await service.VerifyAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), request.Binding,
                request.Attested, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("VerifyProcurementSettlement");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "settlement-review") { if (value != expected) throw ProcurementSettlementRules.Invalid(); }
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementSettlementRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static Money Amount(BsonValue value) => value.IsString ? Money.Parse(value.AsString) : new(value.AsDecimal);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static ProcurementSettlementInputDto Input(BsonValue row) => new(new(row["policyId"].AsInt64), Number(row["policyRevision"]), new(row["bankImportId"].AsInt64), row["bankRowNumber"].AsInt32,
        new(row["observedBeneficiary"]["bankCode"].AsString, row["observedBeneficiary"]["accountNumber"].AsString, row["observedBeneficiary"]["holderName"].AsString, row["observedBeneficiary"]["currency"].AsString),
        row["beneficiaryVerificationReference"].AsString, row["transferVerificationReference"].AsString);
    private static ProcurementDisbursementAuthorizationDto Authorization(BsonValue row) => new(new(row["id"].AsInt64), new(row["requestId"].AsInt64), new(row["decisionId"].AsInt64),
        new(row["orderId"].AsInt64), Number(row["orderVersion"]), new(row["orderApprovalId"].AsInt64), row["route"].AsString, PurchaseOrderEndpoints.Supplier(row["supplier"]),
        Amount(row["authorizedAmount"]), row["businessDate"].AsString, row["neededBy"].AsString, Date(row["approvedAt"]));
    private static ProcurementSettlementDisbursementDto Disbursement(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), new(row["draftId"].AsInt64), R.Location(row["location"]), Authorization(row["authorization"]),
        Amount(row["amount"]), row["transferDate"].AsString, row["transferReference"].AsString, Date(row["recordedAt"]),
        new(new(row["beneficiary"]["supplierId"].AsInt64), new(row["beneficiary"]["beneficiaryId"].AsInt64), new(row["beneficiary"]["versionId"].AsInt64), Number(row["beneficiary"]["version"]), row["beneficiary"]["bankCode"].AsString, row["beneficiary"]["maskedAccount"].AsString, row["beneficiary"]["currency"].AsString),
        new(new(row["payingAccount"]["id"].AsInt64), new(row["payingAccount"]["versionId"].AsInt64), Number(row["payingAccount"]["version"]), row["payingAccount"]["bankCode"].AsString, row["payingAccount"]["bankName"].AsString, row["payingAccount"]["maskedAccount"].AsString, row["payingAccount"]["currency"].AsString),
        new(new(row["original"]["draftId"].AsInt64), Number(row["original"]["draftRevision"]), new(row["original"]["reservationId"].AsInt64),
            new(new(row["original"]["reference"]["id"].AsInt64), Number(row["original"]["reference"]["version"]), row["original"]["reference"]["sha256"].AsString)));
    private static ProcurementSettlementPolicyDto Policy(BsonValue row) => new(new(row["id"].AsInt64), R.Location(row["location"]), Number(row["revision"]), row["active"].AsBoolean, Date(row["approvedAt"]), Date(row["expiresAt"]),
        row["rule"].AsString, row["allowReferenceOnly"].AsBoolean, new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64), row["currency"].AsString, new(row["bookId"].AsInt64), new(row["chartVersionId"].AsInt64),
        row["supplierAdvanceMapping"].AsString, new(row["supplierAdvanceAccountId"].AsInt64), row["supplierAdvanceAccountCode"].AsString, row["cashMapping"].AsString, new(row["cashAccountId"].AsInt64), row["cashAccountCode"].AsString,
        row["bankDebitVerificationProcedureReference"].AsString, row["beneficiaryVerificationProcedureReference"].AsString, row["trustedBankChannelReference"].AsString, row["operatingOwnerReference"].AsString, row["approvalReference"].AsString);
    private static ProcurementSettlementAccountDto Account(BsonValue row) => new(row["mappingKey"].AsString, new(row["accountId"].AsInt64), row["accountCode"].AsString);
    private static ProcurementSettlementAccountingDto Accounting(BsonValue row) => new(new(row["bookId"].AsInt64), new(row["chartVersionId"].AsInt64), new(row["periodId"].AsInt64), row["periodMonth"].AsString, Account(row["supplierAdvance"]), Account(row["cash"]));
    private static ProcurementSettlementDebitDto Debit(BsonValue row) => new(new(row["importId"].AsInt64), row["rowNumber"].AsInt32, row["transactionId"].AsString, row["bookedDate"].AsString, Amount(row["amount"]), row["direction"].AsString, Text(row["reference"]));
    private static ProcurementSettlementSummaryDto Summary(BsonValue row) => new(new(row["id"].AsInt64), new(row["disbursementId"].AsInt64), Amount(row["amount"]), row["status"].AsString, Date(row["recordedAt"]), row["postingDate"].AsString, new(row["journalId"].AsInt64));
    private static ProcurementSettlementPreviewDto Preview(BsonValue row) => new(Disbursement(row["disbursement"]), Input(row["input"]), Policy(row["policy"]), Debit(row["debit"]), Accounting(row["accounting"]), row["postingDate"].AsString, row["binding"].AsString, Date(row["observedAt"]));
    private static ProcurementSettlementViewDto View(BsonValue row) => new(new(row["id"].AsInt64), new(row["disbursementId"].AsInt64), Amount(row["amount"]), row["status"].AsString, Date(row["recordedAt"]), row["postingDate"].AsString, new(row["journalId"].AsInt64),
        Debit(row["debit"]), Policy(row["policy"]), Accounting(row["accounting"]), row["beneficiaryVerificationReference"].AsString, row["transferVerificationReference"].AsString, row["reason"].AsString);
}

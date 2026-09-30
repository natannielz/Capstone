using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankReceiptPreviewInputDto([property:JsonRequired] LongId ImportId,[property:JsonRequired] int RowNumber);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankReceiptObservationDto([property:JsonRequired] string AccountNumber,[property:JsonRequired] string TransactionId,[property:JsonRequired] string BookedDate,[property:JsonRequired] Money Amount) {
    internal BankReceiptObservation Domain() => DateOnly.TryParseExact(BookedDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ? new(AccountNumber,TransactionId,date,Amount) : throw BankReceiptPolicy.Invalid();
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankReceiptConfirmDto([property:JsonRequired] LongId ImportId,[property:JsonRequired] int RowNumber,[property:JsonRequired] string Binding,
    [property:JsonRequired] BankReceiptObservationDto Observed,[property:JsonRequired] string SourceReference,[property:JsonRequired] bool Attested,[property:JsonRequired] string Reason);
public sealed record BankReceiptPolicyDto(LongId Id,string Revision,string TrustedSourceReference,string OperatingOwnerReference,string ApprovalReference,string CashMapping,string SuspenseMapping,DateTimeOffset ExpiresAt);
public sealed record BankReceiptPreviewDto(LongId ImportId,int RowNumber,string Binding,StatementRowDto Row,StatementAccountDto Account,BankReceiptPolicyDto Policy,LongId? ExistingReceiptId);
public sealed record BankReceiptResultDto(LongId ConfirmationId,LongId ReceiptId,LongId ImportId,int RowNumber,bool CreatedReceipt,DateTimeOffset RecordedAt,string Operation);
public sealed record BankReceiptRecoveryDto(string Status,BankReceiptResultDto? Result);
public sealed record BankReceiptViewDto(LongId ReceiptId,LongId ImportId,int RowNumber,string TransactionId,string BookedDate,Money Amount,LongId JournalId,LongId ActorId,DateTimeOffset RecordedAt,BankReceiptPolicyDto Policy);
public sealed record BankReceiptListDto(BankReceiptViewDto[] Items,bool HasMore);
public sealed record BankReceiptImportDto(BankReceiptResultDto[] Items);

public static class BankReceiptEndpoints
{
    private static DateTimeOffset Date(BsonValue value)=>new(value.ToUniversalTime());
    private static Money Amount(BsonValue value)=>new(Decimal128.ToDecimal(value.AsDecimal128));
    private static string? Optional(BsonValue value)=>value.IsBsonNull?null:value.AsString;
    private static BankReceiptPolicyDto Policy(BsonValue row)=>new(new(row["_id"].AsInt64),OrderDraftInputDto.Number(row["revision"].AsInt64),row["trustedSourceReference"].AsString,
        row["operatingOwnerReference"].AsString,row["approvalReference"].AsString,row["cashMapping"].AsString,row["suspenseMapping"].AsString,Date(row["expiresAt"]));
    private static BankReceiptResultDto Result(BsonDocument row)=>new(new(row["confirmationId"].AsInt64),new(row["receiptId"].AsInt64),new(row["importId"].AsInt64),row["rowNumber"].AsInt32,row["createdReceipt"].AsBoolean,Date(row["recordedAt"]),row["operation"].AsString);
    private static BankReceiptViewDto View(BsonDocument row)=>new(new(row["_id"].AsInt64),new(row["importId"].AsInt64),row["rowNumber"].AsInt32,row["transactionId"].AsString,row["bookedDate"].AsString,
        Amount(row["amount"]),new(row["journalId"].AsInt64),new(row["actorId"].AsInt64),Date(row["recordedAt"]),Policy(row["policy"]));
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl="private, no-store, max-age=0"; http.Response.Headers.Pragma="no-cache"; }
    private static void Read(string purpose,HttpContext http) { Private(http); if(purpose!="bank-receipt-review") throw BankReceiptPolicy.Invalid(); }
    public static void MapBankReceiptEndpoints(this WebApplication app) {
        var group=app.MapGroup("/api/v1/billing/bank-receipts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach(var status in new[]{400,401,403,409,503}) group.WithMetadata(new ProducesResponseTypeMetadata(status,typeof(ApiProblem),["application/problem+json"]));
        group.MapPost("/query",async(ReceivingAccountQueryDto request,HttpContext http,BankReceiptService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var rows=await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor,request.AfterId?.Value,ct);
            return TypedResults.Ok(new BankReceiptListDto([..rows.Take(25).Select(View)],rows.Count>25));
        }).WithName("ListBankReceipts");
        group.MapPost("/{id}/query",async(string id,ReceivingAccountReadDto request,HttpContext http,BankReceiptService service,CancellationToken ct)=>{
            Read(request.Purpose,http); return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor,LongId.Parse(id).Value,ct)));
        }).WithName("ReadBankReceipt");
        group.MapPost("/imports/{id}/query",async(string id,ReceivingAccountReadDto request,HttpContext http,BankReceiptService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var rows=await service.ImportConfirmationsAsync(http.Features.Get<ResolvedActor>()!.Actor,LongId.Parse(id).Value,ct);
            return TypedResults.Ok(new BankReceiptImportDto([..rows.Select(Result)]));
        }).WithName("ReadBankReceiptImportConfirmations");
        group.MapPost("/preview",async(BankReceiptPreviewInputDto request,HttpContext http,BankReceiptService service,CancellationToken ct)=>{
            Private(http); var row=await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor,request.ImportId.Value,request.RowNumber,ct); var line=row["row"]; var account=row["account"];
            return TypedResults.Ok(new BankReceiptPreviewDto(new(row["importId"].AsInt64),row["rowNumber"].AsInt32,row["binding"].AsString,
                new(line["number"].AsInt32,Optional(line["transactionId"]),line["bookedDate"].AsString,line["direction"].AsString,Amount(line["amount"]),Optional(line["payerName"]),Optional(line["reference"]),Optional(line["identityCandidate"])),
                new(new(account["accountId"].AsInt64),new(account["_id"].AsInt64),OrderDraftInputDto.Number(account["version"].AsInt64),ReceivingAccountDetailsDto.From(account["details"]),Date(account["effectiveAt"])),
                Policy(row["policy"]),row["existingReceiptId"].IsBsonNull?null:new(row["existingReceiptId"].AsInt64)));
        }).WithName("PreviewBankReceiptConfirmation");
        group.MapPost("/confirmations",async(BankReceiptConfirmDto request,HttpContext http,BankReceiptService service,[FromHeader(Name="If-Match")]string expected,[FromHeader(Name="Idempotency-Key")]string key,CancellationToken ct)=>{
            Private(http); if(request.Observed is null || AccessRequestMetadata.ExpectedVersion(http.Request)!=0) throw BankReceiptPolicy.Invalid();
            return TypedResults.Ok(Result(await service.ConfirmAsync(http.Features.Get<ResolvedActor>()!.Actor,request.ImportId.Value,request.RowNumber,request.Binding,request.Observed.Domain(),request.SourceReference,request.Attested,
                new(AccessRequestMetadata.IdempotencyKey(http.Request),request.Reason,http.TraceIdentifier),ct)));
        }).WithName("ConfirmBankReceipt");
        group.MapPost("/operations/query",async(ReceivingAccountRecoveryDto request,HttpContext http,BankReceiptService service,CancellationToken ct)=>{
            Private(http); if(request.Purpose!="operation-recovery" || request.Operation!="billing.bank-receipt.confirm") throw BankReceiptPolicy.Invalid();
            var result=await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor,request.Key,ct);
            return TypedResults.Ok(new BankReceiptRecoveryDto(result is null?"not_confirmed":"committed",result is null?null:Result(result)));
        }).WithName("RecoverBankReceiptConfirmation");
    }
}

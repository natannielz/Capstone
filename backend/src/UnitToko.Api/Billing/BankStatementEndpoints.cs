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
public sealed record StatementSelectionDto([property:JsonRequired] LongId ProfileId,[property:JsonRequired] string ProfileRevision,[property:JsonRequired] LongId AccountId,[property:JsonRequired] LongId AccountVersionId) {
    internal BankStatementSelection Domain()=>new(ProfileId.Value,LongId.Parse(ProfileRevision).Value,AccountId.Value,AccountVersionId.Value);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatementAccountsQueryDto([property:JsonRequired] string Purpose,[property:JsonRequired] LongId ProfileId,[property:JsonRequired] string ProfileRevision,[property:JsonRequired] LongId? AfterId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatementPreviewDto([property:JsonRequired] StatementSelectionDto Selection,[property:JsonRequired] string OriginalBase64);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatementStageDto([property:JsonRequired] StatementSelectionDto Selection,[property:JsonRequired] string OriginalBase64,[property:JsonRequired] string Filename,
    [property:JsonRequired] string Binding,[property:JsonRequired] string SourceReference,[property:JsonRequired] bool Attested,[property:JsonRequired] string Reason);
public sealed record StatementFormatDto(string Delimiter,string DateFormat,string DecimalSeparator,string CreditMarker,string DebitMarker,string[] Columns,string? IdentityNamespace);
public sealed record StatementProfileDto(LongId Id,string Revision,string Name,string BankCode,StatementFormatDto Format,string TrustedSourceReference,string OperatingOwnerReference,string ApprovalReference,bool Active,DateTimeOffset ApprovedAt,DateTimeOffset ExpiresAt);
public sealed record StatementProfilesDto(StatementProfileDto[] Items,bool HasMore);
public sealed record StatementAccountDto(LongId AccountId,LongId VersionId,string Version,ReceivingAccountDetailsDto Details,DateTimeOffset EffectiveAt);
public sealed record StatementAccountsDto(StatementAccountDto[] Items,bool HasMore);
public sealed record StatementRowDto(int Number,string? TransactionId,string BookedDate,string Direction,Money Amount,string? PayerName,string? Reference,string? IdentityCandidate);
public sealed record StatementIssueDto(int Row,string Field,string Code);
public sealed record StatementParsedDto(string SourceSha256,StatementRowDto[] Rows,StatementIssueDto[] Issues,Money Credits,Money Debits,bool CanStage);
public sealed record StatementPreviewResultDto(string Binding,StatementParsedDto Preview);
public sealed record StatementSummaryDto(LongId ImportId,LongId ProfileId,string ProfileName,LongId AccountId,LongId AccountVersionId,string MaskedAccount,string Filename,int RowCount,int IssueCount,Money Credits,Money Debits,LongId ActorId,DateTimeOffset RecordedAt,string State);
public sealed record StatementImportsDto(StatementSummaryDto[] Items,bool HasMore);
public sealed record StatementDetailDto(LongId ImportId,StatementProfileDto Profile,StatementAccountDto Account,string Filename,string SourceReference,string Reason,LongId ActorId,DateTimeOffset RecordedAt,StatementParsedDto Preview,string State);
public sealed record StatementResultDto(LongId ImportId,string Operation,int RowCount,DateTimeOffset RecordedAt);
public sealed record StatementRecoveryDto(string Status,StatementResultDto? Result);

public static class BankStatementEndpoints
{
    private static DateTimeOffset Date(BsonValue value)=>new(value.ToUniversalTime());
    private static string Number(BsonValue value)=>OrderDraftInputDto.Number(value.AsInt64);
    private static string? Optional(BsonValue value)=>value.IsBsonNull?null:value.AsString;
    private static Money Money(BsonValue value)=>new(Decimal128.ToDecimal(value.AsDecimal128));
    private static StatementProfileDto Profile(BsonValue row) {
        var f=row["format"]; return new(new(row["_id"].AsInt64),Number(row["revision"]),row["name"].AsString,row["bankCode"].AsString,
            new(f["delimiter"].AsString,f["dateFormat"].AsString,f["decimalSeparator"].AsString,f["creditMarker"].AsString,f["debitMarker"].AsString,[..f["columns"].AsBsonArray.Select(value=>value.AsString)],Optional(f["identityNamespace"])),
            row["trustedSourceReference"].AsString,row["operatingOwnerReference"].AsString,row["approvalReference"].AsString,row["active"].AsBoolean,Date(row["approvedAt"]),Date(row["expiresAt"]));
    }
    private static StatementAccountDto Account(BsonValue row)=>new(new(row["accountId"].AsInt64),new(row["_id"].AsInt64),Number(row["version"]),ReceivingAccountDetailsDto.From(row["details"]),Date(row["effectiveAt"]));
    private static StatementParsedDto Parsed(BankStatementPreview row)=>new(row.SourceSha256,[..row.Rows.Select(value=>new StatementRowDto(value.Number,value.TransactionId,value.BookedDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),value.Direction,value.Amount,value.PayerName,value.Reference,value.IdentityCandidate))],
        [..row.Issues.Select(value=>new StatementIssueDto(value.Row,value.Field,value.Code))],row.Credits,row.Debits,row.CanStage);
    private static StatementParsedDto Parsed(BsonValue row)=>new(row["sourceSha256"].AsString,[..row["rows"].AsBsonArray.Select(value=>new StatementRowDto(value["number"].AsInt32,Optional(value["transactionId"]),value["bookedDate"].AsString,value["direction"].AsString,Money(value["amount"]),Optional(value["payerName"]),Optional(value["reference"]),Optional(value["identityCandidate"])))],
        [..row["issues"].AsBsonArray.Select(value=>new StatementIssueDto(value["row"].AsInt32,value["field"].AsString,value["code"].AsString))],Money(row["credits"]),Money(row["debits"]),true);
    private static StatementResultDto Result(BsonDocument row)=>new(new(row["importId"].AsInt64),row["operation"].AsString,row["rowCount"].AsInt32,Date(row["recordedAt"]));
    private static byte[] Original(string value) {
        if (value is null || value.Length is <4 or >349528) throw BankStatementParser.Invalid();
        var buffer=new byte[BankStatementParser.MaximumBytes];
        if (!Convert.TryFromBase64String(value,buffer,out var length) || length==0 || Convert.ToBase64String(buffer.AsSpan(0,length))!=value) throw BankStatementParser.Invalid();
        return buffer.AsSpan(0,length).ToArray();
    }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl="private, no-store, max-age=0"; http.Response.Headers.Pragma="no-cache"; }
    private static void Read(string purpose,HttpContext http) { Private(http); if(purpose!="bank-statement-review") throw BankStatementParser.Invalid(); }
    public static void MapBankStatementEndpoints(this WebApplication app) {
        var group=app.MapGroup("/api/v1/billing/bank-statements").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach(var status in new[]{400,401,403,409,503}) group.WithMetadata(new ProducesResponseTypeMetadata(status,typeof(ApiProblem),["application/problem+json"]));
        group.MapPost("/profiles/query",async(ReceivingAccountQueryDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var rows=await service.ListProfilesAsync(http.Features.Get<ResolvedActor>()!.Actor,request.AfterId?.Value,ct);
            return TypedResults.Ok(new StatementProfilesDto([..rows.Take(25).Select(Profile)],rows.Count>25));
        }).WithName("ListBankStatementProfiles");
        group.MapPost("/accounts/query",async(StatementAccountsQueryDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var rows=await service.ListAccountsAsync(http.Features.Get<ResolvedActor>()!.Actor,request.ProfileId.Value,LongId.Parse(request.ProfileRevision).Value,request.AfterId?.Value,ct);
            return TypedResults.Ok(new StatementAccountsDto([..rows.Take(25).Select(Account)],rows.Count>25));
        }).WithName("ListBankStatementAccounts");
        group.MapPost("/imports/query",async(ReceivingAccountQueryDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var rows=await service.ListImportsAsync(http.Features.Get<ResolvedActor>()!.Actor,request.AfterId?.Value,ct);
            return TypedResults.Ok(new StatementImportsDto([..rows.Take(25).Select(row=>new StatementSummaryDto(new(row["importId"].AsInt64),new(row["profileId"].AsInt64),row["profileName"].AsString,new(row["accountId"].AsInt64),new(row["accountVersionId"].AsInt64),
                row["maskedAccount"].AsString,row["filename"].AsString,row["rowCount"].AsInt32,row["issueCount"].AsInt32,Money(row["credits"]),Money(row["debits"]),new(row["actorId"].AsInt64),Date(row["recordedAt"]),row["state"].AsString))],rows.Count>25));
        }).WithName("ListBankStatementImports");
        group.MapPost("/preview",async(StatementPreviewDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Private(http); if(request.Selection is null) throw BankStatementParser.Invalid();
            var result=await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor,request.Selection.Domain(),Original(request.OriginalBase64),ct);
            return TypedResults.Ok(new StatementPreviewResultDto(result.Binding,Parsed(result.Preview)));
        }).WithName("PreviewBankStatement");
        group.MapPost("/imports",async(StatementStageDto request,HttpContext http,BankStatementService service,[FromHeader(Name="If-Match")]string expected,[FromHeader(Name="Idempotency-Key")]string key,CancellationToken ct)=>{
            Private(http); if(request.Selection is null || AccessRequestMetadata.ExpectedVersion(http.Request)!=0) throw BankStatementParser.Invalid();
            var result=await service.StageAsync(http.Features.Get<ResolvedActor>()!.Actor,request.Selection.Domain(),Original(request.OriginalBase64),request.Filename,request.Binding,request.SourceReference,request.Attested,
                new(AccessRequestMetadata.IdempotencyKey(http.Request),request.Reason,http.TraceIdentifier),ct); return TypedResults.Ok(Result(result));
        }).WithName("StageBankStatement");
        group.MapPost("/imports/{id}/query",async(string id,ReceivingAccountReadDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Read(request.Purpose,http); var row=await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor,LongId.Parse(id).Value,ct);
            return TypedResults.Ok(new StatementDetailDto(new(row["_id"].AsInt64),Profile(row["profile"]),Account(row["account"]),row["filename"].AsString,row["sourceReference"].AsString,row["reason"].AsString,new(row["actorId"].AsInt64),Date(row["recordedAt"]),Parsed(row["preview"]),"staged"));
        }).WithName("ReadBankStatementImport");
        group.MapPost("/imports/{id}/original/query",async(string id,ReceivingAccountReadDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Read(request.Purpose,http); if(http.Request.Headers.ContainsKey("Range")) throw BankStatementParser.Invalid(); var selected=LongId.Parse(id);
            var bytes=await service.ReadOriginalAsync(http.Features.Get<ResolvedActor>()!.Actor,selected.Value,ct);
            http.Response.Headers.XContentTypeOptions="nosniff"; return TypedResults.File(bytes,"application/octet-stream","bank-statement-"+selected+".csv",enableRangeProcessing:false);
        }).WithName("ReadBankStatementOriginal");
        group.MapPost("/operations/query",async(ReceivingAccountRecoveryDto request,HttpContext http,BankStatementService service,CancellationToken ct)=>{
            Private(http); if(request.Purpose!="operation-recovery" || request.Operation!="billing.bank-statement.stage") throw BankStatementParser.Invalid();
            var result=await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor,request.Key,ct); return TypedResults.Ok(new StatementRecoveryDto(result is null?"not_confirmed":"committed",result is null?null:Result(result)));
        }).WithName("RecoverBankStatementImport");
    }
}

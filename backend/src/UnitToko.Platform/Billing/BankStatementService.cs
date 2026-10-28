using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Platform.Billing;

public sealed record BankStatementSelection(long ProfileId,long ProfileRevision,long AccountId,long AccountVersionId);
public sealed record BankStatementPreviewResult(string Binding,BankStatementPreview Preview);

// Private staged source records only. Confirmation, cash journals and allocation belong to later
// independent reconciliation commands; importing any number of files never recognizes money.
public sealed partial class BankStatementService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly BankStatementRecords records;
    private readonly ReceivingAccountService accounts;
    public BankStatementService(IMongoDatabase database,MongoUnitOfWork unit,IdAllocator ids,IClock clock,ReceivingAccountService accounts) {
        if (!ReferenceEquals(database.Client,accounts.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(accounts.SourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Statement and receiving account owners must share one transaction");
        this.database=database; this.clock=clock; this.ids=ids; this.accounts=accounts; authorization=new(database,unit,clock); records=new(database);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    internal static string[] PermissionsFor(bool stage) => stage ? [Permissions.BankStatementRead,Permissions.BankStatementStage,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead]
        : [Permissions.BankStatementRead,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead];
    private sealed record Context(ActorContext Actor,AuthorizationSnapshot Authority,IClientSessionHandle Tx,bool Stage,BsonDocument? Scope=null);
    private AuthorizationQuery[] Require(Context context) {
        var queries=PermissionsFor(context.Stage).Select(permission => { var query=ScopePolicy.Compile(context.Actor,context.Authority,permission,clock.UtcNow); AuthorizationStore.Require(query); return query; }).ToArray();
        if (context.Scope is { } scope) { var resource=ReceivingAccountBson.Scope(scope); if (!ScopePolicy.IsResourceActive(resource,context.Authority.Directory) || queries.Any(query=>!query.Matches(resource))) throw new AuthorizationDeniedException("scope_denied"); }
        return queries;
    }
    private async Task StorageAsync(CancellationToken ct) {
        await accounts.RequireSourceStorageAsync(ct);
        foreach (var name in BankStatementSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database,name,BankStatementSchemas.Validator(name),BankStatementSchemas.Key(name),ct);
            var definition=await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter=new BsonDocument("name",name) },ct)).SingleAsync(ct);
            if (definition["options"].AsBsonDocument.Contains("encryptedFields")) throw B.Broken();
        }
    }
    private async Task FenceAsync(Context context,CancellationToken ct) {
        var queries=Require(context); var resource=context.Scope is null ? null : ReceivingAccountBson.Scope(context.Scope);
        var clauses=queries.SelectMany(query=>query.Clauses.Where(clause=>resource is null || clause.Matches(resource))).ToArray();
        await new HistoricalAuthorizationFence(database,authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause=>clause.RequiredNodes).Concat(resource is null ? [] : ScopePolicy.RequiredNodes(resource,context.Authority.Directory)).Distinct(),[],
            context.Authority.Grants.Where(grant=>clauses.Any(clause=>clause.GrantId==grant.Id)),context.Authority.Memberships.Where(member=>clauses.Any(clause=>clause.MembershipId==member.Id)),context.Tx,ct);
        if (context.Actor.Session is { } session) {
            var live=await records.Rows("identitySessions").Find(context.Tx,SessionPersistence.LiveFilter(session,context.Actor.UserId,context.Actor.ProviderIssuer,context.Actor.ProviderSubject,context.Actor.GrantVersion,clock.UtcNow)).SingleOrDefaultAsync(ct);
            var now=clock.UtcNow.UtcDateTime;
            if (live is null || live["idleExpiresAt"].ToUniversalTime()<=now || live["absoluteExpiresAt"].ToUniversalTime()<=now) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _=Require(context); ct.ThrowIfCancellationRequested();
    }
    private static void Select(BankStatementSelection selected) {
        if (selected is null || selected.ProfileId<=0 || selected.ProfileRevision<=0 || selected.AccountId<=0 || selected.AccountVersionId<=0) throw BankStatementParser.Invalid();
    }
    private async Task<(Context Context,BsonDocument Profile,BsonDocument Account)> SourceAsync(Context context,BankStatementSelection selected,CancellationToken ct) {
        _=Require(context);
        var profile=await records.RowAsync(context.Tx,B.Profiles,selected.ProfileId,ct); context=context with { Scope=profile["scope"].AsBsonDocument }; _=Require(context);
        if (profile["revision"]!=selected.ProfileRevision) throw B.Changed(); BankStatementRecords.ProfileAt(profile,Now);
        var account=await accounts.ReadSourceAsync(context.Actor,context.Authority,context.Tx,selected.AccountId,selected.AccountVersionId,true,ct);
        if (account["scope"]!=context.Scope || account["details"]["bankCode"]!=profile["bankCode"]) throw B.Changed();
        return(context,profile,account);
    }
    private BankStatementPreview Parse(byte[] original,BsonDocument profile,BsonDocument account) => BankStatementParser.Parse(original,B.Format(profile["format"]),
        ReceivingAccountBson.Details(account["details"]),account["physicalIdentity"].AsString,AccountingCalendar.BusinessDate(Now));
    private static byte[] Copy(byte[] original) {
        if (original is null || original.Length is <1 or >BankStatementParser.MaximumBytes) throw BankStatementParser.Invalid(); return (byte[])original.Clone();
    }
    private async Task AuditReadAsync(Context context,string operation,long? id,CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(context.Tx,new BsonDocument {
        { "_id",(await ids.NextAsync("auditEvents",ct)).Value }, { "schemaVersion",1 }, { "kind",B.Prefix+"read" }, { "operation",operation },
        { "actorId",context.Actor.UserId }, { "actorGrantVersion",context.Actor.GrantVersion }, { "importId",id is { } value ? new BsonInt64(value) : BsonNull.Value }, { "createdAt",Now.UtcDateTime } },cancellationToken:ct);
    public async Task<BankStatementPreviewResult> PreviewAsync(ActorContext actor,BankStatementSelection selected,byte[] original,CancellationToken ct=default) {
        Select(selected); original=Copy(original); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var (context,profile,account)=await SourceAsync(new(actor,authority,tx,true),selected,token); var parsed=Parse(original,profile,account);
            await records.TouchAsync(tx,B.Profiles,profile,token); await AuditReadAsync(context,B.Prefix+"preview",null,token); await FenceAsync(context,token);
            BankStatementRecords.ProfileAt(profile,Now); return new BankStatementPreviewResult(B.Binding(profile,account,parsed.SourceSha256),parsed);
        },ct);
    }
    public async Task<BsonDocument> StageAsync(ActorContext actor,BankStatementSelection selected,byte[] original,string filename,string binding,string sourceReference,bool attested,AuthorizationCommandMetadata metadata,CancellationToken ct=default) {
        Select(selected); original=Copy(original); AuthorizationStore.ValidateMetadata(metadata);
        if (!attested || !BankStatementParser.Text(filename,160) || filename.IndexOfAny(['/', '\\'])>=0 || !BankStatementParser.Text(sourceReference,256) ||
            !BankStatementParser.Text(metadata.Reason,1000) || !BankStatementParser.Text(metadata.CorrelationId,128) || metadata.IdempotencyKey.Length>128 ||
            metadata.IdempotencyKey.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) || binding is null || binding.Length!=64 || binding.Any(c=>!char.IsAsciiHexDigitLower(c))) throw BankStatementParser.Invalid();
        await StorageAsync(ct); var id=(await ids.NextAsync(B.Imports,ct)).Value; var auditId=(await ids.NextAsync("auditEvents",ct)).Value;
        var body=B.Body(selected.ProfileId,selected.ProfileRevision,selected.AccountId,selected.AccountVersionId,filename,BankStatementParser.Digest(original),binding,sourceReference,attested);
        Context? captured=null; BsonDocument? profile=null; BsonDocument? account=null; BankStatementPreview? parsed=null; PersonAuthorityProof? proof=null; bool fresh=false;
        return await authorization.ExecuteCommandAsync(actor,B.Operation,metadata,body,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,true); _=Require(context); proof=null; fresh=false; profile=null; parsed=null;
            var saved=await records.ForKeyAsync(tx,actor.UserId,metadata.IdempotencyKey,token);
            if (saved is not null) { context=context with { Scope=saved["scope"].AsBsonDocument }; _=Require(context); account=await HistoricalAccountAsync(context,saved,token); }
            else { (context,profile,account)=await SourceAsync(context,selected,token); parsed=Parse(original,profile,account);
                if (!parsed.CanStage || B.Binding(profile,account,parsed.SourceSha256)!=binding) throw B.Changed(); fresh=true; }
            captured=context; var resource=ReceivingAccountBson.Scope(context.Scope!);
            return new(ScopePolicy.Authorize(actor,authority,Permissions.BankStatementStage,resource,clock.UtcNow),ScopePolicy.RequiredNodes(resource,authority.Directory),Permissions.BankStatementStage,new(resource));
        },async (tx,token)=>{
            var context=captured ?? throw B.Broken(); var at=Now;
            proof=await new PersonRegistryService(database,clock).ResolveProofsAsync(tx,[actor.UserId],token); var person=proof.Evidence.Single();
            if (person.Verification==PersonVerification.Verified) await new PersonAuthorizationFence(database,clock).TouchAsync(tx,proof,token);
            else if (person.Verification!=PersonVerification.Unknown) throw new AuthorizationDeniedException("person_evidence_unavailable");
            var row=new BsonDocument { { "_id",id }, { "schemaVersion",1 }, { "scope",context.Scope! }, { "profile",B.Snapshot(profile!) }, { "account",account! }, { "binding",binding },
                { "original",new BsonBinaryData(original) }, { "filename",filename }, { "sourceReference",sourceReference }, { "attested",true }, { "state","staged" }, { "preview",B.Preview(parsed!) },
                { "actorId",actor.UserId }, { "actorGrantVersion",actor.GrantVersion }, { "personAtImport",ReceivingAccountBson.Person(person) }, { "operation",B.Operation },
                { "requestKey",metadata.IdempotencyKey }, { "bodyHash",AuthorizationStore.CommandHash(body,metadata.Reason) }, { "reason",metadata.Reason }, { "correlationId",metadata.CorrelationId },
                { "auditId",auditId }, { "recordedAt",at.UtcDateTime }, { "fence",0L } }; row.Add("snapshotHash",B.Hash(row));
            await records.Rows(B.Imports).InsertOneAsync(tx,row,cancellationToken:token); await records.Rows("auditEvents").InsertOneAsync(tx,B.Audit(row),cancellationToken:token);
            await records.Rows("outboxEvents").InsertOneAsync(tx,new BsonDocument { { "_id",ObjectId.GenerateNewId() }, { "schemaVersion",1 }, { "kind",B.Operation }, { "sourceKey",B.SourceKey(row) },
                { "actorId",actor.UserId }, { "requestKey",metadata.IdempotencyKey }, { "payload",B.Result(row) }, { "createdAt",at.UtcDateTime }, { "dueAt",at.UtcDateTime }, { "status","pending" }, { "attempts",0 } },cancellationToken:token);
            return B.Result(row);
        },ct,async (_,tx,token)=>{
            var context=captured ?? throw B.Broken(); var saved=await records.ForKeyAsync(tx,actor.UserId,metadata.IdempotencyKey,token) ?? throw B.Broken();
            if (saved["bodyHash"]!=AuthorizationStore.CommandHash(body,metadata.Reason)) throw B.Broken();
            await HistoricalAccountAsync(context,saved,token); await records.TouchAsync(tx,B.Imports,saved,token);
            if (fresh) { var source=await SourceAsync(context,selected,token); if (B.Hash(source.Profile)!=B.Hash(profile!)) throw B.Changed(); await records.TouchAsync(tx,B.Profiles,source.Profile,token); }
            await FenceAsync(context,token);
        },validateCurrent:()=>{ _=Require(captured ?? throw B.Broken()); if (fresh) BankStatementRecords.ProfileAt(profile!,Now); if (proof?.Evidence.Single().Verification==PersonVerification.Verified) proof.RequireCurrent(captured!.Tx,clock.UtcNow); });
    }
    private async Task<BsonDocument> HistoricalAccountAsync(Context context,BsonDocument row,CancellationToken ct) {
        var account=row["account"].AsBsonDocument;
        var stored=await accounts.ReadSourceAsync(context.Actor,context.Authority,context.Tx,account["accountId"].AsInt64,account["_id"].AsInt64,false,ct);
        BankStatementRecords.Equal(stored,account); return stored;
    }
}

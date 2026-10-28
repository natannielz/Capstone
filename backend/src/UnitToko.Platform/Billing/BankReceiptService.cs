using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

public sealed partial class BankReceiptService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly AuthorizationStore authorization;
    private readonly BankReceiptRecords records;
    private readonly BankStatementService statements;
    public BankReceiptService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, BankStatementService statements) {
        if (!ReferenceEquals(database.Client,statements.ReceiptDatabase.Client) || !database.DatabaseNamespace.Equals(statements.ReceiptDatabase.DatabaseNamespace)) throw new ArgumentException("Receipt and statement owners must share one transaction");
        this.database=database; this.clock=clock; this.ids=ids; this.statements=statements; authorization=new(database,unit,clock); records=new(database,statements);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed record Context(ActorContext Actor, AuthorizationSnapshot Authority, IClientSessionHandle Tx, bool Confirm, BsonDocument? Scope = null);
    internal static string[] Required(bool confirm) => confirm ? [Permissions.BankReceiptConfirm,Permissions.BankReceiptRead,Permissions.BankStatementRead,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead]
        : [Permissions.BankReceiptRead,Permissions.BankStatementRead,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead];
    private AuthorizationQuery[] Require(Context context) => Required(context.Confirm).Select(permission => {
        var query=ScopePolicy.Compile(context.Actor,context.Authority,permission,clock.UtcNow); AuthorizationStore.Require(query);
        if (context.Scope is { } scope) { var resource=ReceivingAccountBson.Scope(scope); if (!ScopePolicy.IsResourceActive(resource,context.Authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied"); }
        return query;
    }).ToArray();
    private async Task StorageAsync(CancellationToken ct) {
        await statements.RequireReceiptStorageAsync(ct);
        foreach (var name in BankReceiptSchemas.Collections) {
            var definition=await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter=new BsonDocument("name",name) },ct)).SingleOrDefaultAsync(ct);
            if (definition is null || definition.GetValue("type","")!="collection") throw B.Broken();
            var options=definition.GetValue("options",new BsonDocument()).AsBsonDocument;
            if (options.GetValue("capped",false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.GetValue("validationLevel","strict")!="strict" || options.GetValue("validationAction","error")!="error" ||
                options.TryGetValue("collation",out var collation) && !InvoiceStorage.Simple(collation) ||
                !InventorySchemas.Contains(options.GetValue("validator",new BsonDocument()).AsBsonDocument,BankReceiptSchemas.Validator(name))) throw B.Broken();
            var indexes=await (await records.Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys=BankReceiptSchemas.Keys(name);
            bool Matches(BsonDocument index,BsonDocument key)=>index["key"].Equals(key) && index.GetValue("unique",false).AsBoolean && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression");
            if (!indexes.Any(index=>index["name"]=="_id_" && index["key"].Equals(new BsonDocument("_id",1)) && !InvoiceStorage.Unsafe(index)) ||
                keys.Any(key=>!indexes.Any(index=>Matches(index,key))) || indexes.Any(index=>InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index.GetValue("unique",false).AsBoolean && index["name"]!="_id_" && !keys.Any(key=>Matches(index,key)))) throw B.Broken();
        }
        await ProcurementBankClaimSchemas.RequireStorageAsync(database, ct);
    }

    private async Task FenceAsync(Context context,CancellationToken ct) {
        var queries=Require(context); var resource=context.Scope is null ? null : ReceivingAccountBson.Scope(context.Scope);
        var clauses=queries.SelectMany(query=>query.Clauses.Where(clause=>resource is null || clause.Matches(resource))).ToArray();
        await new HistoricalAuthorizationFence(database,authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause=>clause.RequiredNodes).Concat(resource is null ? [] : ScopePolicy.RequiredNodes(resource,context.Authority.Directory)).Distinct(),[],
            context.Authority.Grants.Where(grant=>clauses.Any(clause=>clause.GrantId==grant.Id)),context.Authority.Memberships.Where(member=>clauses.Any(clause=>clause.MembershipId==member.Id)),context.Tx,ct);
        if (context.Actor.Session is { } session) {
            var live=await records.Rows("identitySessions").Find(context.Tx,SessionPersistence.LiveFilter(session,context.Actor.UserId,context.Actor.ProviderIssuer,context.Actor.ProviderSubject,context.Actor.GrantVersion,clock.UtcNow)).SingleOrDefaultAsync(ct);
            var at=clock.UtcNow.UtcDateTime;
            if (live is null || live["idleExpiresAt"].ToUniversalTime()<=at || live["absoluteExpiresAt"].ToUniversalTime()<=at) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _=Require(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<(Context Context,BankStatementReceiptSource Source)> SourceAsync(Context context,long importId,int rowNumber,bool current,CancellationToken ct) {
        _=Require(context);
        // Determine the private scope before following source history or exposing any details.
        var raw=await records.Rows(BankStatementBson.Imports).Find(context.Tx,new BsonDocument("_id",importId)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        context=context with { Scope=raw["scope"].AsBsonDocument }; _=Require(context);
        return(context,await statements.ReceiptSourceAsync(context.Actor,context.Authority,context.Tx,importId,rowNumber,current,ct));
    }
    private async Task<BsonDocument> PolicyAsync(Context context,BankStatementReceiptSource source,CancellationToken ct) {
        var policy=await records.Rows(B.Policies).Find(context.Tx,new BsonDocument("physicalIdentity",source.Import["account"]["physicalIdentity"])).SingleOrDefaultAsync(ct) ?? throw BankReceiptPolicy.Invalid("bank_receipt_policy_unavailable");
        policy=await records.RowAsync(context.Tx,B.Policies,policy["_id"].AsInt64,ct); BankReceiptRecords.Source(source,policy,Now);
        var anchor=await records.AnchorAsync(context.Tx,policy["physicalIdentity"].AsString,ct);
        if (anchor is not null && (anchor["scope"]!=policy["scope"] || anchor["identityNamespace"]!=policy["identityNamespace"])) throw B.Changed("bank_receipt_namespace_locked");
        if (anchor is not null) await records.TouchAsync(context.Tx,B.Accounts,anchor,ct);
        return policy;
    }
    private async Task<PersonAuthorityProof> PeopleAsync(Context context,BankStatementReceiptSource source,CancellationToken ct) {
        var maker=source.Import["actorId"].AsInt64;
        if (maker==context.Actor.UserId) throw BankReceiptPolicy.Invalid("independent_person_required");
        var proof=await new PersonRegistryService(database,clock).ResolveProofsAsync(context.Tx,[context.Actor.UserId,maker],ct);
        BankReceiptPolicy.Independent(proof.Evidence.Single(p=>p.UserId==context.Actor.UserId),proof.Evidence.Single(p=>p.UserId==maker));
        await new PersonAuthorizationFence(database,clock).TouchAsync(context.Tx,proof,ct); return proof;
    }
    private static void Selection(long importId,int rowNumber) { if (importId<=0 || rowNumber is <2 or >501) throw BankReceiptPolicy.Invalid(); }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length>128 || metadata.IdempotencyKey.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            !BankStatementParser.Text(metadata.Reason,1000) || !BankStatementParser.Text(metadata.CorrelationId,128)) throw BankReceiptPolicy.Invalid();
    }
    private async Task<BsonDocument?> ExistingAsync(Context context,BankStatementReceiptSource source,CancellationToken ct) {
        var canonical=source.Row["identityCandidate"];
        var claims = new BankTransactionClaims(database);
        await claims.RequireNoOrphanSettlementAsync(context.Tx, ct);
        var claim = await claims.ReadAsync(context.Tx, canonical.AsString, ct);
        if (claim is not null && claim["direction"] != "credit") throw B.Changed("bank_transaction_already_claimed");
        var row=await records.Rows(B.Receipts).Find(context.Tx,new BsonDocument("canonicalIdentity",canonical)).SingleOrDefaultAsync(ct);
        if (row is null) {
            if (claim is not null || await records.Rows(B.Confirmations).Find(context.Tx,new BsonDocument("canonicalIdentity",canonical)).AnyAsync(ct)) throw B.Broken();
            await claims.RequireUnclaimedAsync(context.Tx, canonical.AsString, ct);
            await claims.RequireNoOrphanCreditAsync(context.Tx, canonical.AsString, ct); return null;
        }
        row=await records.ReceiptAsync(context.Actor,context.Authority,context.Tx,row["_id"].AsInt64,ct); BankReceiptRecords.SameTransfer(row,source); return row;
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor,long importId,int rowNumber,CancellationToken ct=default) {
        Selection(importId,rowNumber); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var (context,source)=await SourceAsync(new(actor,authority,tx,true),importId,rowNumber,true,token); var policy=await PolicyAsync(context,source,token);
            var proof=await PeopleAsync(context,source,token); var existing=await ExistingAsync(context,source,token);
            var already=await records.Rows(B.Confirmations).Find(tx,new BsonDocument { { "importId",importId }, { "rowNumber",rowNumber } }).AnyAsync(token);
            if (already) throw B.Changed("bank_receipt_row_already_reviewed");
            await records.TouchAsync(tx,B.Policies,policy,token); await AuditReadAsync(context,"preview",importId,token); await FenceAsync(context,token);
            BankReceiptRecords.PolicyAt(policy,Now); BankStatementRecords.ProfileAt(source.CurrentProfile!,Now); proof.RequireCurrent(tx,clock.UtcNow);
            return new BsonDocument { { "importId",importId }, { "rowNumber",rowNumber }, { "binding",B.Binding(source,policy) }, { "row",source.Row },
                { "account",source.Import["account"] }, { "policy",B.Snapshot(policy) }, { "existingReceiptId",existing is null ? BsonNull.Value : existing["_id"] } };
        },ct);
    }
    public async Task<BsonDocument> ConfirmAsync(ActorContext actor,long importId,int rowNumber,string binding,BankReceiptObservation observed,string sourceReference,bool attested,AuthorizationCommandMetadata metadata,CancellationToken ct=default) {
        Selection(importId,rowNumber); observed=BankReceiptPolicy.Copy(observed); Metadata(metadata);
        if (!attested || !BankStatementParser.Text(sourceReference,256) || binding is not { Length:64 } || binding.Any(c=>!char.IsAsciiHexDigitLower(c))) throw BankReceiptPolicy.Invalid();
        await StorageAsync(ct); var confirmationId=(await ids.NextAsync(B.Confirmations,ct)).Value; var receiptId=(await ids.NextAsync(B.Receipts,ct)).Value;
        var anchorId=(await ids.NextAsync(B.Accounts,ct)).Value; var auditId=(await ids.NextAsync("auditEvents",ct)).Value;
        var body=B.Body(importId,rowNumber,binding,observed,sourceReference,attested);
        Context? captured=null; BankStatementReceiptSource? source=null; BsonDocument? policy=null; PersonAuthorityProof? proof=null;
        BsonDocument? capturedExisting=null; bool fresh=false;
        return await authorization.ExecuteCommandAsync(actor,B.Operation,metadata,body,async (authority,tx,token)=>{
            policy=null; proof=null; capturedExisting=null; fresh=false;
            var context=new Context(actor,authority,tx,true); _=Require(context);
            var saved=await records.ForKeyAsync(tx,actor.UserId,metadata.IdempotencyKey,token);
            (context,source)=await SourceAsync(context,saved?["importId"].AsInt64 ?? importId,saved?["rowNumber"].AsInt32 ?? rowNumber,saved is null,token);
            if (saved is null) {
                policy=await PolicyAsync(context,source,token); if (B.Binding(source,policy)!=binding) throw B.Changed();
                // Prove all retained canonical owners and reverse orphan effects before
                // AuthorizationStore inserts this command's own provisional receipt.
                // This exact source/owner proof remains in the same transaction snapshot.
                capturedExisting=await ExistingAsync(context,source,token); fresh=true;
            }
            captured=context; var resource=ReceivingAccountBson.Scope(context.Scope!); var approval=new ApprovalRequest(1,1,RecordedBy:source.Import["actorId"].AsInt64);
            // Person authority is resolved and fenced by AuthorizationStore as well as the owner proof retained below.
            var queries=Require(context);
            return new(new(true,"allowed",queries.SelectMany(q=>q.Clauses.Where(c=>c.Matches(resource)).Select(c=>c.GrantId)).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(resource,authority.Directory),Permissions.BankReceiptConfirm,new(resource,approval));
        },async (tx,token)=>{
            var context=captured ?? throw B.Broken(); var actual=source ?? throw B.Broken(); var approved=policy ?? throw B.Broken();
            if (!fresh || !ReferenceEquals(context.Tx,tx) || !tx.IsInTransaction) throw B.Broken();
            BankReceiptPolicy.RequireMatch(observed,actual.Import["account"]["details"]["accountNumber"].AsString,B.Line(actual.Row));
            proof=await PeopleAsync(context,actual,token);
            if (await records.Rows(B.Confirmations).Find(tx,new BsonDocument { { "importId",importId }, { "rowNumber",rowNumber } }).AnyAsync(token)) throw B.Changed("bank_receipt_row_already_reviewed");
            var existing=capturedExisting; var at=Now; var created=existing is null; var finalReceipt=existing?["_id"].AsInt64 ?? receiptId;
            if (created) {
                var anchor=await records.AnchorAsync(tx,approved["physicalIdentity"].AsString,token);
                if (anchor is null) {
                    anchor=new BsonDocument { { "_id",anchorId }, { "schemaVersion",1 }, { "scope",context.Scope! }, { "physicalIdentity",approved["physicalIdentity"] },
                        { "identityNamespace",approved["identityNamespace"] }, { "firstReceiptId",receiptId }, { "createdAt",at.UtcDateTime }, { "fence",0L } };
                    anchor.Add("snapshotHash",B.Hash(anchor)); await records.Rows(B.Accounts).InsertOneAsync(tx,anchor,cancellationToken:token);
                } else await records.TouchAsync(tx,B.Accounts,anchor,token);
                var receipt=new BsonDocument { { "_id",receiptId }, { "schemaVersion",1 }, { "scope",context.Scope! }, { "canonicalIdentity",actual.Row["identityCandidate"] },
                    { "physicalIdentity",approved["physicalIdentity"] }, { "identityNamespace",approved["identityNamespace"] }, { "transactionId",actual.Row["transactionId"] },
                    { "bookedDate",actual.Row["bookedDate"] }, { "amount",actual.Row["amount"] }, { "importId",importId }, { "rowNumber",rowNumber },
                    { "importHash",actual.Import["snapshotHash"] }, { "rowHash",B.Hash(actual.Row) }, { "confirmationId",confirmationId }, { "actorId",actor.UserId },
                    { "recordedAt",at.UtcDateTime }, { "policy",B.Snapshot(approved) }, { "journalId",BsonNull.Value }, { "journalFingerprint",BsonNull.Value },
                    { "snapshotHash",BsonNull.Value }, { "postingFence",0L }, { "fence",0L } };
                await records.Rows(B.Receipts).InsertOneAsync(tx,receipt,cancellationToken:token);
                var journal=await new PostJournal(database,clock,[new BankReceiptPostingSource(database,receipt)]).PostAsync(tx,actor,BankReceiptPostingSource.Source(receiptId),1,new(metadata.Reason,metadata.CorrelationId),token);
                receipt["journalId"]=journal.Id; receipt["journalFingerprint"]=journal.Fingerprint; receipt["postingFence"]=1L; receipt["snapshotHash"]=B.Hash(receipt);
                if ((await records.Rows(B.Receipts).ReplaceOneAsync(tx,new BsonDocument { { "_id",receiptId }, { "postingFence",1L }, { "journalId",BsonNull.Value } },receipt,cancellationToken:token)).ModifiedCount!=1) throw B.Changed();
            } else await records.TouchAsync(tx,B.Receipts,existing!,token);
            var row=new BsonDocument { { "_id",confirmationId }, { "schemaVersion",1 }, { "scope",context.Scope! }, { "receiptId",finalReceipt }, { "canonicalIdentity",actual.Row["identityCandidate"] },
                { "createdReceipt",created }, { "importId",importId }, { "rowNumber",rowNumber }, { "importHash",actual.Import["snapshotHash"] }, { "rowHash",B.Hash(actual.Row) },
                { "binding",binding }, { "policy",B.Snapshot(approved) }, { "observed",B.Observation(observed) }, { "sourceReference",sourceReference }, { "attested",true },
                { "people",new BsonArray(proof.Evidence.OrderBy(p=>p.UserId).Select(ReceivingAccountBson.Person)) }, { "actorId",actor.UserId }, { "actorGrantVersion",actor.GrantVersion },
                { "operation",B.Operation }, { "requestKey",metadata.IdempotencyKey }, { "bodyHash",AuthorizationStore.CommandHash(body,metadata.Reason) }, { "reason",metadata.Reason },
                { "correlationId",metadata.CorrelationId }, { "auditId",auditId }, { "recordedAt",at.UtcDateTime }, { "fence",0L } };
            row.Add("snapshotHash",B.Hash(row)); await records.Rows(B.Confirmations).InsertOneAsync(tx,row,cancellationToken:token);
            await records.Rows("auditEvents").InsertOneAsync(tx,B.Audit(row),cancellationToken:token);
            await records.Rows("outboxEvents").InsertOneAsync(tx,new BsonDocument { { "_id",ObjectId.GenerateNewId() }, { "schemaVersion",1 }, { "kind",B.Operation }, { "sourceKey",B.SourceKey(row) },
                { "actorId",actor.UserId }, { "requestKey",metadata.IdempotencyKey }, { "payload",B.Result(row) }, { "createdAt",at.UtcDateTime }, { "dueAt",at.UtcDateTime }, { "status","pending" }, { "attempts",0 } },cancellationToken:token);
            var claimedReceipt = await records.Rows(B.Receipts).Find(tx, new BsonDocument("_id", finalReceipt)).SingleOrDefaultAsync(token) ?? throw B.Broken();
            var claims = new BankTransactionClaims(database);
            if (created) await claims.ClaimCreditAsync(tx, claimedReceipt, token);
            else await claims.RequireCreditAsync(tx, claimedReceipt, token);
            return B.Result(row);
        },ct,async (authority,tx,token)=>{
            var context=captured ?? throw B.Broken(); var saved=await records.ForKeyAsync(tx,actor.UserId,metadata.IdempotencyKey,token) ?? throw B.Broken();
            var verified=await records.ConfirmationAsync(actor,context.Authority,tx,saved["_id"].AsInt64,token);
            if (saved["bodyHash"]!=AuthorizationStore.CommandHash(body,metadata.Reason)) throw B.Broken();
            proof=await PeopleAsync(context,verified.Source,token); await records.TouchAsync(tx,B.Confirmations,verified.Row,token);
            if (fresh) {
                source=(await SourceAsync(context,importId,rowNumber,true,token)).Source; var current=await PolicyAsync(context,source,token);
                if (B.Hash(current)!=B.Hash(policy!)) throw B.Changed(); await records.TouchAsync(tx,B.Policies,current,token);
                _ = await ExistingAsync(context, source, token);
            }
            await FenceAsync(context,token);
        },validateCurrent:()=>{ _=Require(captured ?? throw B.Broken()); if (fresh) { BankReceiptRecords.PolicyAt(policy!,Now); BankStatementRecords.ProfileAt(source!.CurrentProfile!,Now); } proof?.RequireCurrent(captured!.Tx,clock.UtcNow); });
    }
    private async Task AuditReadAsync(Context context,string action,long? sourceId,CancellationToken ct) => await records.Rows("auditEvents").InsertOneAsync(context.Tx,new BsonDocument {
        { "_id",(await ids.NextAsync("auditEvents",ct)).Value }, { "schemaVersion",1 }, { "kind",B.Prefix+"read" }, { "operation",B.Prefix+action }, { "actorId",context.Actor.UserId },
        { "actorGrantVersion",context.Actor.GrantVersion }, { "sourceId",sourceId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "createdAt",Now.UtcDateTime } },cancellationToken:ct);
}

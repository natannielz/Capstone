using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Platform.Billing;

public sealed partial class BankStatementService
{
    private async Task<IReadOnlyList<BsonDocument>> ListAsync(ActorContext actor,string collection,long? afterId,CancellationToken ct) {
        if (afterId is <=0) throw BankStatementParser.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<IReadOnlyList<BsonDocument>>(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false); var queries=Require(context);
            var scoped=new BsonDocument("$and",new BsonArray(queries.Select(query=>ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),BsonSerializer.SerializerRegistry)))));
            var pipeline=new List<BsonDocument> { new("$replaceRoot",new BsonDocument("newRoot",new BsonDocument("$mergeObjects",new BsonArray { "$scope",new BsonDocument("_id","$_id") }))), new("$match",scoped) };
            if (afterId.HasValue) pipeline.Add(new("$match",new BsonDocument("_id",new BsonDocument("$gt",afterId.Value))));
            pipeline.Add(new("$sort",new BsonDocument("_id",1))); pipeline.Add(new("$limit",26));
            var selected=await records.Rows(collection).Aggregate<BsonDocument>(tx,pipeline).ToListAsync(token); var result=new List<BsonDocument>();
            foreach (var item in selected) {
                var raw=await records.RowAsync(tx,collection,item["_id"].AsInt64,token); var own=context with { Scope=raw["scope"].AsBsonDocument }; _=Require(own);
                if (collection==B.Profiles) { BankStatementParser.Validate(B.Format(raw["format"])); result.Add(B.Snapshot(raw)); }
                else {
                    var row=await records.ImportAsync(tx,raw["_id"].AsInt64,token); await HistoricalAccountAsync(own,row,token);
                    result.Add(new BsonDocument { { "importId",row["_id"] }, { "profileId",row["profile"]["_id"] }, { "profileName",row["profile"]["name"] },
                        { "accountId",row["account"]["accountId"] }, { "accountVersionId",row["account"]["_id"] }, { "maskedAccount",ReceivingAccountPolicy.Mask(ReceivingAccountBson.Details(row["account"]["details"])) },
                        { "filename",row["filename"] }, { "rowCount",row["preview"]["rows"].AsBsonArray.Count }, { "issueCount",row["preview"]["issues"].AsBsonArray.Count },
                        { "credits",row["preview"]["credits"] }, { "debits",row["preview"]["debits"] }, { "actorId",row["actorId"] }, { "recordedAt",row["recordedAt"] }, { "state","staged" } });
                }
                await records.TouchAsync(tx,collection,raw,token);
            }
            await AuditReadAsync(context,B.Prefix+"list-"+collection,null,token); await FenceAsync(context,token); return result;
        },ct);
    }
    public Task<IReadOnlyList<BsonDocument>> ListProfilesAsync(ActorContext actor,long? afterId=null,CancellationToken ct=default) => ListAsync(actor,B.Profiles,afterId,ct);
    public Task<IReadOnlyList<BsonDocument>> ListImportsAsync(ActorContext actor,long? afterId=null,CancellationToken ct=default) => ListAsync(actor,B.Imports,afterId,ct);
    public async Task<IReadOnlyList<BsonDocument>> ListAccountsAsync(ActorContext actor,long profileId,long profileRevision,long? afterId=null,CancellationToken ct=default) {
        if (profileId<=0 || profileRevision<=0 || afterId is <=0) throw BankStatementParser.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false); _=Require(context);
            var profile=await records.RowAsync(tx,B.Profiles,profileId,token); context=context with { Scope=profile["scope"].AsBsonDocument }; _=Require(context);
            if (profile["revision"]!=profileRevision) throw B.Changed(); BankStatementRecords.ProfileAt(profile,Now);
            var result=await accounts.ListSourcesAsync(actor,authority,tx,context.Scope!,profile["bankCode"].AsString,afterId,token);
            await records.TouchAsync(tx,B.Profiles,profile,token); await AuditReadAsync(context,B.Prefix+"accounts",null,token); await FenceAsync(context,token);
            BankStatementRecords.ProfileAt(profile,Now); return result;
        },ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor,long id,CancellationToken ct=default) {
        if (id<=0) throw BankStatementParser.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false); _=Require(context);
            var raw=await records.RowAsync(tx,B.Imports,id,token); context=context with { Scope=raw["scope"].AsBsonDocument }; _=Require(context);
            var row=await records.ImportAsync(tx,id,token); await HistoricalAccountAsync(context,row,token);
            await records.TouchAsync(tx,B.Imports,row,token); await AuditReadAsync(context,B.Prefix+"detail",id,token); await FenceAsync(context,token);
            // Original bytes are returned only by an explicit, separately audited request below.
            var result=row.DeepClone().AsBsonDocument; result.Remove("original"); result.Remove("personAtImport"); return result;
        },ct);
    }
    public async Task<byte[]> ReadOriginalAsync(ActorContext actor,long id,CancellationToken ct=default) {
        if (id<=0) throw BankStatementParser.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false); _=Require(context);
            var raw=await records.RowAsync(tx,B.Imports,id,token); context=context with { Scope=raw["scope"].AsBsonDocument }; _=Require(context);
            var row=await records.ImportAsync(tx,id,token); await HistoricalAccountAsync(context,row,token);
            await records.TouchAsync(tx,B.Imports,row,token); await AuditReadAsync(context,B.Prefix+"original",id,token); await FenceAsync(context,token);
            return (byte[])row["original"].AsByteArray.Clone();
        },ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor,string key,CancellationToken ct=default) {
        AuthorizationStore.ValidateMetadata(new(key,"Recover staged statement","bank-statement-recovery"));
        if (key.Length>128 || key.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw BankStatementParser.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,true); _=Require(context);
            var row=await records.ForKeyAsync(tx,actor.UserId,key,token);
            if (row is not null) { context=context with { Scope=row["scope"].AsBsonDocument }; _=Require(context); await HistoricalAccountAsync(context,row,token); await records.TouchAsync(tx,B.Imports,row,token); }
            await AuditReadAsync(context,B.Prefix+"recovery",row?["_id"].AsInt64,token); await FenceAsync(context,token); return row is null ? null : B.Result(row);
        },ct);
    }
}

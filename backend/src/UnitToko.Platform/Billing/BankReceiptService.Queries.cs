using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

public sealed partial class BankReceiptService
{
    private async Task<List<BsonDocument>> ReceiptIdsAsync(Context context, long? afterId, CancellationToken ct) {
        var queries = Require(context);
        var scoped = new BsonDocument("$and", new BsonArray(queries.Select(query => ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)))));
        var pipeline = new List<BsonDocument> { new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scope", new BsonDocument("_id", "$_id") }))), new("$match", scoped) };
        if (afterId.HasValue) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$gt", afterId.Value))));
        pipeline.Add(new("$sort", new BsonDocument("_id", 1))); pipeline.Add(new("$limit", 26));
        return await records.Rows(B.Receipts).Aggregate<BsonDocument>(context.Tx, pipeline).ToListAsync(ct);
    }
    public async Task<IReadOnlyList<BsonDocument>> ListAsync(ActorContext actor,long? afterId=null,CancellationToken ct=default) {
        if (afterId is <=0) throw BankReceiptPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<IReadOnlyList<BsonDocument>>(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false);
            var selected=await ReceiptIdsAsync(context,afterId,token); var result=new List<BsonDocument>();
            foreach (var item in selected) {
                var raw=await records.RowAsync(tx,B.Receipts,item["_id"].AsInt64,token); _=Require(context with { Scope=raw["scope"].AsBsonDocument });
                var row=await records.ReceiptAsync(actor,authority,tx,item["_id"].AsInt64,token); await records.TouchAsync(tx,B.Receipts,row,token); result.Add(row);
            }
            await AuditReadAsync(context,"list",null,token); await FenceAsync(context,token); return result;
        },ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor,long id,CancellationToken ct=default) {
        if (id<=0) throw BankReceiptPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,false); _=Require(context); var raw=await records.RowAsync(tx,B.Receipts,id,token);
            context=context with { Scope=raw["scope"].AsBsonDocument }; _=Require(context);
            var row=await records.ReceiptAsync(actor,authority,tx,id,token); await records.TouchAsync(tx,B.Receipts,row,token);
            await AuditReadAsync(context,"detail",id,token); await FenceAsync(context,token); return row;
        },ct);
    }
    public async Task<IReadOnlyList<BsonDocument>> ImportConfirmationsAsync(ActorContext actor,long importId,CancellationToken ct=default) {
        if (importId<=0) throw BankReceiptPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<IReadOnlyList<BsonDocument>>(actor,async (authority,tx,token)=>{
            // Row 2 is guaranteed for any accepted nonempty import; it is used only to authorize its private scope.
            var (context,_)=await SourceAsync(new(actor,authority,tx,false),importId,2,false,token);
            var rows=await records.Rows(B.Confirmations).Find(tx,new BsonDocument("importId",importId)).Sort(new BsonDocument("rowNumber",1)).Limit(501).ToListAsync(token);
            if (rows.Count>500) throw B.Broken(); var result=new List<BsonDocument>();
            foreach(var row in rows) { var verified=await records.ConfirmationAsync(actor,authority,tx,row["_id"].AsInt64,token); result.Add(B.Result(verified.Row)); }
            await AuditReadAsync(context,"import-confirmations",importId,token); await FenceAsync(context,token); return result;
        },ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor,string key,CancellationToken ct=default) {
        Metadata(new(key,"Recover bank confirmation","bank-receipt-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor,async (authority,tx,token)=>{
            var context=new Context(actor,authority,tx,true); _=Require(context); var saved=await records.ForKeyAsync(tx,actor.UserId,key,token);
            UnitToko.Platform.Identity.PersonAuthorityProof? proof=null;
            if (saved is not null) {
                context=context with { Scope=saved["scope"].AsBsonDocument }; _=Require(context);
                var verified=await records.ConfirmationAsync(actor,authority,tx,saved["_id"].AsInt64,token); proof=await PeopleAsync(context,verified.Source,token);
                await records.TouchAsync(tx,B.Confirmations,verified.Row,token);
            }
            await AuditReadAsync(context,"recovery",saved?["_id"].AsInt64,token); await FenceAsync(context,token); proof?.RequireCurrent(tx,clock.UtcNow);
            return saved is null ? null : B.Result(saved);
        },ct);
    }
}

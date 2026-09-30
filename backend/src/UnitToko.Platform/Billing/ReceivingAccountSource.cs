using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Billing;

public sealed partial class ReceivingAccountService
{
    internal IMongoDatabase SourceDatabase => database;
    internal Task RequireSourceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<IReadOnlyList<BsonDocument>> ListSourcesAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, BsonDocument scope, string bankCode, long? afterId, CancellationToken ct) {
        var context = new Context(actor,snapshot,tx,Permissions.ReceivingAccountDetailsRead,scope); _=Require(context);
        var filter = new BsonDocument { { "scope",scope }, { "version",new BsonDocument("$gt",0L) } };
        if (afterId.HasValue) filter.Add("_id",new BsonDocument("$gt",afterId.Value));
        // Filter bank before pagination: approved versions retain the same operational scope.
        BsonDocument[] pipeline = [new("$match",filter),new("$lookup",new BsonDocument { { "from",B.Versions }, { "localField","versionId" }, { "foreignField","_id" }, { "as","approved" } }),
            new("$match",new BsonDocument("approved.details.bankCode",bankCode)),new("$sort",new BsonDocument("_id",1)),new("$limit",26)];
        var selected=await Rows(B.Heads).Aggregate<BsonDocument>(tx,pipeline).ToListAsync(ct); var result=new List<BsonDocument>();
        foreach (var row in selected) result.Add(await ReadSourceAsync(actor,snapshot,tx,row["_id"].AsInt64,row["versionId"].AsInt64,true,ct));
        await CompleteAsync(context,ct); return result;
    }
    // An owning financial operation selects an explicit immutable version. Current is required for a
    // new binding; historical recovery never retargets the old record to the current bank details.
    internal async Task<BsonDocument> ReadSourceAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long accountId, long versionId, bool current, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.ReceivingAccountDetailsRead, clock.UtcNow));
        var raw = await records.RowAsync(tx, B.Heads, accountId, ct);
        var context = new Context(actor, snapshot, tx, Permissions.ReceivingAccountDetailsRead, raw["scope"].AsBsonDocument);
        _ = Require(context); context.Head = await records.HeadAsync(tx, accountId, ct);
        var version = await records.RowAsync(tx, B.Versions, versionId, ct);
        if (version["accountId"] != accountId || version["scope"] != context.Scope ||
            current && context.Head["versionId"] != versionId || version["effectiveAt"].ToUniversalTime() > Now.UtcDateTime) throw B.Changed();
        await CompleteAsync(context, ct); return version.DeepClone().AsBsonDocument;
    }
}

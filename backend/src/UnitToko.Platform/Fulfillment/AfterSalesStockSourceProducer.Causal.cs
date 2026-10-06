using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Fulfillment;
internal sealed partial class AfterSalesStockSourceProducer
{
    internal async Task InitializeCausalAsync(IClientSessionHandle tx, AfterSalesStockSourceEvent setup, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        var poolId = setup.PoolIds.Single();
        if (setup.Kind != "setup" || await Rows(AfterSalesStockSourceRecords.CausalHeads).Find(tx, new BsonDocument("_id", poolId)).AnyAsync(ct) ||
            await Rows(AfterSalesStockSourceRecords.CausalTransitions).Find(tx, new BsonDocument("poolId", poolId)).AnyAsync(ct)) throw AfterSalesStockSourceRecords.Unavailable();
        Time(setup);
        var transition = await CausalTransitionAsync(setup, poolId, null, "setup", 1L, 0L, 0L, ct);
        await Rows(AfterSalesStockSourceRecords.CausalTransitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        await Rows(AfterSalesStockSourceRecords.CausalHeads).InsertOneAsync(tx, new BsonDocument {
            { "_id", poolId }, { "schemaVersion", 1 }, { "kind", "aftersales.causal.head" },
            { "location", setup.Record["proposal"]["input"]["location"].DeepClone() }, { "setupDecisionId", setup.Id }, { "revision", 1L },
            { "lastTransitionId", transition["_id"] }, { "lastHash", transition["transitionHash"] }, { "updatedAt", setup.Time }, { "fence", 0L } }, cancellationToken: ct);
    }
    internal async Task<Action> RecordCausalFreshAsync(IClientSessionHandle tx, ActorContext actor, AuthorizationSnapshot authority,
        AuthorizationStore authorization, string kind, BsonDocument fresh, Action current, CancellationToken ct)
    {
        await AfterSalesStockSourceStorage.RequireCoverageAsync(database, ct); new InventoryRepository(database).RequireSession(tx); current();
        var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, fresh["_id"].AsInt64, ct);
        await owner.FenceTopologyAsync(tx, root["source"].AsBsonDocument, ct);
        var source = await owner.ReadCausalAsync(tx, kind, fresh["_id"].AsInt64, ct);
        var saved = source.Record[kind == "request" ? "case" : "decision"];
        if (saved["snapshotHash"] != fresh["snapshotHash"]) throw AfterSalesStockSourceRecords.Unavailable();
        Time(source); var writes = new List<AfterSalesStockSourcePoolWrite>();
        foreach (var poolId in source.PoolIds) {
            var causal = await records.CausalHistoryAsync(tx, owner, poolId, ct);
            await records.RequireCausalSourcesAsync(tx, owner, poolId, causal.History, source, ct);
            var physical = await records.HistoryAsync(tx, owner, poolId, ct);
            await records.RequireSourcesAsync(tx, owner, poolId, physical.History, null, ct);
            if (causal.Head["revision"].AsInt64 >= AfterSalesStockSourceRecords.MaximumRows || causal.Head["fence"].AsInt64 >= long.MaxValue - 2 ||
                source.Time.CompareTo(causal.Head["updatedAt"]) < 0) throw AfterSalesStockSourceRecords.Unavailable();
            var before = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct) ?? throw AfterSalesStockSourceRecords.Unavailable();
            var proof = await AfterSalesStockSourcePoolWrite.FenceAsync(database, authorization, tx, actor, authority, source, before,
                kind == "request" ? Permissions.CaseRequest : Permissions.CaseManage, current, clock, ct);
            if (causal.Head["location"] != proof.Location || proof.PoolRevision < causal.History[^1]["poolRevision"].AsInt64 ||
                proof.BeforeFence < causal.History[^1]["afterPoolFence"].AsInt64) throw AfterSalesStockSourceRecords.Unavailable();
            await proof.RequireAsync(database, tx, ct);
            await AppendCausalAsync(tx, source, poolId, causal.Head, causal.History[^1], "live", proof.PoolRevision, proof.BeforeFence, proof.AfterFence, ct);
            await proof.RequireAsync(database, tx, ct); proof.Consume(); writes.Add(proof);
        }
        // Case requests do not require approval-person evidence. Refresh their actual persistent
        // session predicate after the last pool/history CAS as well as their current grant clock.
        await authorization.FenceAsync(actor, [], tx, ct);
        current(); ct.ThrowIfCancellationRequested();
        return () => { current(); foreach (var write in writes) write.RequireCurrent(); Time(source); };
    }
    internal async Task AppendCausalAsync(IClientSessionHandle tx, AfterSalesStockSourceEvent source, long poolId, BsonDocument head,
        BsonDocument previous, string origin, long poolRevision, long before, long after, CancellationToken ct)
    {
        Time(source);
        if (!source.PoolIds.Contains(poolId) || source.Time.CompareTo(previous["recordedAt"]) < 0 || head["revision"].AsInt64 >= AfterSalesStockSourceRecords.MaximumRows)
            throw AfterSalesStockSourceRecords.Unavailable();
        var transition = await CausalTransitionAsync(source, poolId, previous, origin, poolRevision, before, after, ct);
        await Rows(AfterSalesStockSourceRecords.CausalTransitions).InsertOneAsync(tx, transition, cancellationToken: ct);
        if ((await Rows(AfterSalesStockSourceRecords.CausalHeads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("revision", 1L).Inc("fence", 1L)
            .Set("lastTransitionId", transition["_id"]).Set("lastHash", transition["transitionHash"]).Set("updatedAt", source.Time), cancellationToken: ct)).ModifiedCount != 1)
            throw AfterSalesStockSourceRecords.Changed();
    }
    private async Task<BsonDocument> CausalTransitionAsync(AfterSalesStockSourceEvent source, long poolId, BsonDocument? previous,
        string origin, long revision, long before, long after, CancellationToken ct)
    {
        var row = new BsonDocument { { "_id", (await ids.NextAsync(AfterSalesStockSourceRecords.CausalTransitions, ct)).Value }, { "schemaVersion", 1 },
            { "kind", "aftersales.causal.transition" }, { "poolId", poolId }, { "revision", previous is null ? 1L : checked(previous["revision"].AsInt64 + 1) },
            { "previousId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["transitionHash"] },
            { "sourceKind", source.Kind }, { "sourceId", source.Id }, { "sourceHash", source.Hash }, { "sourceAuditId", source.Audit["_id"] },
            { "actorId", source.Audit["actorId"] }, { "actorGrantVersion", source.Audit["actorGrantVersion"] }, { "recordedAt", source.Time },
            { "poolRevision", revision }, { "beforePoolFence", before }, { "afterPoolFence", after }, { "caseId", source.Kind == "setup" ? BsonNull.Value : new BsonInt64(source.Id) }, { "origin", origin } };
        row.Add("transitionHash", AfterSalesStockSourceRecords.Hash(row)); return row;
    }
}

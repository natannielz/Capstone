using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Reconstructs one previously committed request from owned history. Recovery requires
    /// its receipt inside the replay transaction and cannot dispatch a new business writer.</summary>
    public async Task<BsonDocument?> RecoverRevisionAsync(ActorContext actor, StockLocation location, string operation, string key,
        string correlationId, CancellationToken ct = default)
    {
        location.Validate();
        if (!StockSourceSetupPolicy.Text(key, 200) || !StockSourceSetupPolicy.Text(correlationId, 128) || operation is not
            ("stock.source.revision.propose" or "stock.source.revision.approve" or "stock.source.revision.reject" or "stock.source.revision.withdraw"))
            throw StockSourceRevisionBson.Invalid();
        await RevisionStorageAsync(ct);
        var proposed = operation == "stock.source.revision.propose";
        var reviewed = operation is "stock.source.revision.approve" or "stock.source.revision.reject";
        var permission = reviewed ? Permissions.StockSourceApprove : Permissions.StockSourcePropose;
        var recovery = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, permission, Resource(location));
            var receipt = await Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? result = null;
            if (receipt is not null)
            {
                try
                {
                    if (receipt.ElementCount != (reviewed ? 9 : 8) || !receipt["_id"].IsObjectId ||
                        !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"].AsInt32 != 1 || !receipt["actorId"].IsInt64 ||
                        !receipt["result"].IsBsonDocument || !receipt["result"]["proposalId"].IsInt64 || receipt["result"]["proposalId"].AsInt64 <= 0)
                        throw StockSourceRevisionBson.Invalid();
                    var proposalId = receipt["result"]["proposalId"].AsInt64;
                    var scoped = await Rows("stockSourceRevisionProposals").Find(tx, F.Eq("_id", proposalId))
                        .Project(new BsonDocument("input.location", 1)).SingleOrDefaultAsync(token);
                    if (scoped is null) throw StockSourceRevisionBson.Invalid();
                    if (InventoryGraph.Location(scoped["input"]["location"].AsBsonDocument) != location)
                        throw new AuthorizationDeniedException("scope_denied");
                    var view = await RevisionViewAsync(tx, proposalId, token);
                    var proposal = view["proposal"].AsBsonDocument;
                    var row = proposed ? proposal : view["decision"].AsBsonDocument;
                    BsonDocument expected;
                    object body;
                    if (proposed)
                    {
                        if (proposal["proposedBy"].AsInt64 != actor.UserId) throw StockSourceRevisionBson.Invalid();
                        var poolId = proposal["poolId"].AsInt64; var expectedPoolRevision = proposal["basePoolRevision"].AsInt64;
                        var input = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
                        expected = new() { { "proposalId", proposalId }, { "revision", 1L }, { "poolId", poolId }, { "basePoolRevision", expectedPoolRevision } };
                        body = new { poolId, expectedPoolRevision, input };
                    }
                    else
                    {
                        if (row["decidedBy"].AsInt64 != actor.UserId || operation != "stock.source.revision." + row["decision"].AsString)
                            throw StockSourceRevisionBson.Invalid();
                        var expectedRevision = row["revision"].AsInt64; var decision = RevisionOutcome(operation);
                        var reviewReference = row["reviewReference"].AsString;
                        body = new { proposalId, expectedRevision, decision, reviewReference };
                        expected = new() { { "proposalId", proposalId }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] },
                            { "poolRevision", row["poolRevision"] }, { "status", row["decision"] } };
                    }
                    if (!receipt["result"].Equals(expected) || receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) ||
                        StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[proposed ? "proposedAt" : "decidedAt"]))
                        throw StockSourceRevisionBson.Invalid();
                    if (reviewed)
                    {
                        var participants = PersonApprovalReceipt.Read(receipt, Permissions.StockSourceApprove);
                        if (participants != new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64)) throw StockSourceRevisionBson.Invalid();
                    }
                    else if (receipt.Contains(PersonApprovalReceipt.Field)) throw StockSourceRevisionBson.Invalid();
                    result = new() { { "proposal", proposal }, { "row", row }, { "expected", expected } };
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
                { throw StockSourceRevisionBson.Invalid(); }
            }
            await CompleteAsync(actor, snapshot, permission, Resource(location), tx, token);
            return result;
        }, ct);
        if (recovery is null) return null;
        var stored = recovery["row"].AsBsonDocument;
        var replayed = proposed
            ? await ProposeRevisionCoreAsync(actor, stored["poolId"].AsInt64, stored["basePoolRevision"].AsInt64,
                StockSourceSetupBson.ReadInput(stored["input"].AsBsonDocument), new(key, stored["reason"].AsString, correlationId), true, ct)
            : await DecideRevisionCoreAsync(actor, stored["proposalId"].AsInt64, stored["revision"].AsInt64,
                RevisionOutcome(operation), stored["reviewReference"].AsString, new(key, stored["reason"].AsString, correlationId), true, ct);
        if (!replayed.Equals(recovery["expected"])) throw StockSourceRevisionBson.Invalid();
        return replayed;
    }
    private static StockSourceSetupDecision RevisionOutcome(string operation) => operation switch
    {
        "stock.source.revision.approve" => StockSourceSetupDecision.Approve,
        "stock.source.revision.reject" => StockSourceSetupDecision.Reject,
        "stock.source.revision.withdraw" => StockSourceSetupDecision.Withdraw,
        _ => throw StockSourceRevisionBson.Invalid()
    };

    public async Task<AuthorizedPage> ListSourcesAsync(ActorContext actor, StockLocation location, int offset, int limit,
        CancellationToken ct = default)
    {
        location.Validate(); AuthorizationStore.ValidatePage(offset, limit); await RevisionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var filter = SourceLocationFilter(location, "location");
            var total = await Rows("inventoryPools").CountDocumentsAsync(tx, filter, cancellationToken: token);
            var pools = await Rows("inventoryPools").Find(tx, filter).Sort(FSort).Skip(offset).Limit(limit)
                .Project(new BsonDocument("_id", 1)).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var pool in pools) items.Add(await CurrentSourceAsync(tx, pool["_id"].AsInt64, token));
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, Resource(location), tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    public async Task<AuthorizedPage> ListRevisionsAsync(ActorContext actor, StockLocation location, long? poolId, int offset, int limit,
        CancellationToken ct = default)
    {
        location.Validate(); AuthorizationStore.ValidatePage(offset, limit);
        if (poolId is <= 0) throw StockSourceRevisionBson.Invalid();
        await RevisionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var filter = SourceLocationFilter(location, "input.location");
            if (poolId.HasValue) filter &= F.Eq("poolId", poolId.Value);
            var total = await Rows("stockSourceRevisionProposals").CountDocumentsAsync(tx, filter, cancellationToken: token);
            var proposals = await Rows("stockSourceRevisionProposals").Find(tx, filter).Sort(FSort).Skip(offset).Limit(limit)
                .Project(new BsonDocument("_id", 1)).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var proposal in proposals) items.Add(await RevisionViewAsync(tx, proposal["_id"].AsInt64, token));
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, Resource(location), tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    private static FilterDefinition<BsonDocument> SourceLocationFilter(StockLocation location, string prefix) =>
        F.Eq(prefix + ".organizationId", location.OrganizationId.Value) & F.Eq(prefix + ".shopId", location.ShopId.Value) &
        F.Eq(prefix + ".warehouseId", location.WarehouseId.Value) & F.Eq(prefix + ".channelId", location.ChannelId.Value);
}

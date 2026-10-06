using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;
internal sealed partial class AfterSalesStockSourceRecords
{
    internal async Task<(BsonDocument Head, BsonDocument[] History, long OpenCount)> CausalHistoryAsync(IClientSessionHandle tx,
        AfterSalesStockSourceEvents owner, long poolId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        var head = await Rows(CausalHeads).Find(tx, F.Eq("_id", poolId) & AfterSalesCausalCoverageSchemas.Validator(CausalHeads)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await Rows(CausalTransitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var open = new HashSet<long>(); var requests = new HashSet<long>(); var decisions = new HashSet<long>();
        BsonDocument? previous = null; BsonValue? location = null;
        foreach (var row in history) {
            if (!await Rows(CausalTransitions).Find(tx, F.Eq("_id", row["_id"]) & AfterSalesCausalCoverageSchemas.Validator(CausalTransitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) || row["transitionHash"] != Hash(row) ||
                previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if ((previous is null) != (kind == "setup")) throw Unavailable();
            var source = await owner.ReadCausalAsync(tx, kind, id, ct);
            if (!source.PoolIds.Contains(poolId) || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] || row["recordedAt"] != source.Time) throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L ||
                    !row["caseId"].IsBsonNull || row["origin"] != "setup") throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                var before = row["beforePoolFence"].AsInt64; var after = row["afterPoolFence"].AsInt64;
                if (row["caseId"] != id || row["origin"] == "setup" ||
                    (row["origin"] == "live" ? after != checked(before + 1) : after != before) ||
                    row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || before < previous["afterPoolFence"].AsInt64 ||
                    previous["origin"] == "live" && row["origin"] == "retained") throw Unavailable();
                if (kind == "request") { if (!requests.Add(id) || !open.Add(id)) throw Unavailable(); }
                else {
                    if (!requests.Contains(id) || !decisions.Add(id) || !open.Remove(id)) throw Unavailable();
                    var decision = source.Record["decision"];
                    if (decision["state"] == "return_authorized") {
                        var authorized = new BsonArray(source.Record["stock"].AsBsonArray.Where(line => decision["input"]["lines"].AsBsonArray.Any(item =>
                            item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"])));
                        if (AfterSalesStockSourceEvents.CausalPools(authorized).Contains(poolId)) open.Add(id);
                    }
                }
            }
            previous = row;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        return (head, history.ToArray(), open.Count);
    }
    internal async Task RequireCausalSourcesAsync(IClientSessionHandle tx, AfterSalesStockSourceEvents owner, long poolId,
        BsonDocument[] history, AfterSalesStockSourceEvent? pending, CancellationToken ct)
    {
        await owner.OrphansAsync(tx, ct);
        var actual = new HashSet<string> { "setup:" + history[0]["sourceId"].AsInt64 };
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var cases = await Rows(AfterSalesCaseRecords.Collection).Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (cases.Count > MaximumRows) throw Unavailable();
        foreach (var candidate in cases) {
            var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
            if (!UnitToko.Platform.Billing.PaymentDraftBson.Scopes(root["source"]["scopes"].AsBsonArray)
                .Any(scope => scope.FulfillmentOrganizationId == pool["location"]["organizationId"].AsInt64)) continue;
            var source = await owner.ReadCausalAsync(tx, "request", candidate["_id"].AsInt64, ct);
            if (!source.PoolIds.Contains(poolId)) continue;
            actual.Add("request:" + source.Id);
            if (await new AfterSalesDecisionRecords(database).ReadAsync(tx, root, ct) is not null) {
                _ = await owner.ReadCausalAsync(tx, "decision", source.Id, ct);
                actual.Add("decision:" + source.Id);
            }
        }
        var authenticated = history.Select(row => row["sourceKind"].AsString + ":" + row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (!pending.PoolIds.Contains(poolId) || !authenticated.Add(pending.Kind + ":" + pending.Id))) throw Unavailable();
        if (!actual.SetEquals(authenticated)) throw Unavailable();
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Version-18 historical checks. Current grants/person bindings do not prove past authority.</summary>
internal static class StockSourceSuspensionMigrationGraph
{
    private const string Collection = StockSourceSuspensionSchemas.Collection;
    private const string Operation = StockSourceSuspensionBson.Operation;
    internal static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument> One(string name, BsonDocument filter) => await Rows(name).Find(filter).SingleOrDefaultAsync(ct) ?? throw Invalid("missing " + name);
        async Task Source(BsonDocument row)
        {
            var pool = await One("inventoryPools", new("_id", row["poolId"]));
            InventoryGraph.RequirePool(pool);
            if (pool["active"].AsBoolean || row["poolRevision"].AsInt64 > pool["revision"].AsInt64 || !pool["location"].Equals(row["ownerLocation"]))
                throw Invalid("suspended physical pool or settings head");
            var registration = await One("inventoryPoolRegistrations", new("_id", row["poolId"]));
            var source = row["configuration"]["source"].AsBsonDocument;
            if (source["creationProposalId"] != registration["proposalId"] || row["physicalKey"] != registration["physicalKey"]) throw Invalid("creation link");
            BsonDocument input; BsonValue approvedAt;
            if (row["poolRevision"] == 1L)
            {
                var proposal = await One("stockSourceProposals", new("_id", registration["proposalId"]));
                var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
                input = proposal["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
            }
            else
            {
                var revision = await One("stockSourceRevisions", new() { { "poolId", row["poolId"] }, { "poolRevision", row["poolRevision"] } });
                if (revision["proposalId"] != source["revisionProposalId"]) throw Invalid("settings proposal link");
                var decision = await One("stockSourceRevisionDecisions", new("_id", revision["decisionId"]));
                input = revision["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
            }
            if (!source["input"].Equals(input) || source["approvedAt"] != approvedAt) throw Invalid("settings snapshot");
            if (row["poolRevision"].AsInt64 < pool["revision"].AsInt64)
            {
                var next = await One("stockSourceRevisions", new() { { "poolId", row["poolId"] }, { "poolRevision", checked(row["poolRevision"].AsInt64 + 1) } });
                var decision = await One("stockSourceRevisionDecisions", new("_id", next["decisionId"]));
                if (Time(decision["decidedAt"]) < Time(row["suspendedAt"])) throw Invalid("already replaced source settings");
            }
        }
        async Task Mappings(BsonDocument row)
        {
            var mappings = row["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument)
                .ToDictionary(value => value["requestedLocation"]["channelId"].AsInt64);
            foreach (var mapping in mappings.Values)
            {
                var alias = await One("stockSourceAliases", new("_id", mapping["aliasId"]));
                if (alias["poolId"] != row["poolId"] || alias["proposalId"] != mapping["proposalId"] || alias["decisionId"] != mapping["decisionId"] ||
                    alias["aliasRevision"] != mapping["aliasRevision"] || alias["poolRevision"] != mapping["poolRevision"] || alias["enabled"] != mapping["enabled"] ||
                    !alias["requestedLocation"].Equals(mapping["requestedLocation"]) || !alias["ownerLocation"].Equals(row["ownerLocation"]) ||
                    alias["physicalKey"] != row["physicalKey"]) throw Invalid("mapping snapshot");
                var decision = await One("stockSourceAliasDecisions", new("_id", alias["decisionId"]));
                if (Time(decision["decidedAt"]) > Time(row["suspendedAt"])) throw Invalid("future mapping");
            }
            // Every channel/version demonstrably committed before the stop must be represented.
            // Equal millisecond timestamps cannot establish commit order; do not invent such an order.
            using var decisions = await Rows("stockSourceAliasDecisions").FindAsync(new BsonDocument
            {
                { "poolId", row["poolId"] }, { "decision", "approve" }, { "decidedAt", new BsonDocument("$lt", row["suspendedAt"]) }
            }, new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await decisions.MoveNextAsync(ct)) foreach (var decision in decisions.Current)
            {
                await lease.RenewAsync(ct);
                var channel = decision["requestedLocation"]["channelId"].AsInt64;
                if (!mappings.TryGetValue(channel, out var mapping) || mapping["aliasRevision"].AsInt64 < decision["aliasRevision"].AsInt64)
                    throw Invalid("missing or replaced channel mapping");
            }
        }
        async Task Event(BsonDocument row)
        {
            StockSourceSuspensionBson.Require(row);
            var identity = await One("identities", new("_id", row["suspendedBy"]));
            if (!identity["_id"].IsInt64) throw Invalid("historical actor type");
            await Source(row); await Mappings(row);
            Audit(await One("auditEvents", new("_id", row["auditId"])), row);
            var receipts = await Rows("idempotencyRecords").Find(new BsonDocument
                { { "operation", Operation }, { "result.poolId", row["poolId"] }, { "result.suspensionId", row["_id"] } }).Limit(2).ToListAsync(ct);
            if (receipts.Count != 1) throw Invalid("missing or duplicated suspension receipt");
            Receipt(receipts[0], row);
        }
        long poolId = 0, revision = 0; BsonDocument? previous = null;
        using (var events = await Rows(Collection).FindAsync(new BsonDocument(), new FindOptions<BsonDocument>
            { BatchSize = 64, Sort = new BsonDocument { { "poolId", 1 }, { "suspensionRevision", 1 } } }, ct))
            while (await events.MoveNextAsync(ct)) foreach (var row in events.Current)
            {
                await lease.RenewAsync(ct);
                StockSourceSuspensionBson.Require(row);
                if (row["poolId"].AsInt64 != poolId) { poolId = row["poolId"].AsInt64; revision = 0; previous = null; }
                if (revision == long.MaxValue || row["suspensionRevision"].AsInt64 != ++revision) throw Invalid("suspension sequence");
                if (previous is not null) Continuity(previous, row);
                await Event(row); previous = row;
            }
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" })
        {
            var alternatives = new BsonArray { new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.suspend(?:\.|$)")) };
            if (name == "auditEvents")
            {
                alternatives.Add(new BsonDocument("kind", "stock.source.suspension.audit"));
                alternatives.Add(new BsonDocument("record.kind", "stock.source.suspension"));
                alternatives.Add(new BsonDocument("record.suspensionRevision", new BsonDocument("$exists", true)));
            }
            else alternatives.Add(new BsonDocument("result.suspensionId", new BsonDocument("$exists", true)));
            using var cursor = await Rows(name).FindAsync(new BsonDocument("$or", alternatives), new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var record in cursor.Current)
            {
                await lease.RenewAsync(ct);
                if (name == "auditEvents")
                {
                    if (!record.TryGetValue("record", out var nested) || !nested.IsBsonDocument) throw Invalid("orphan suspension audit");
                    var row = await One(Collection, new("_id", nested["_id"]));
                    Audit(record, row);
                }
                else
                {
                    if (!record.TryGetValue("result", out var result) || !result.IsBsonDocument ||
                        !StockSourceSuspensionBson.Positive(result.AsBsonDocument, "suspensionId")) throw Invalid("orphan suspension receipt");
                    Receipt(record, await One(Collection, new("_id", result["suspensionId"])));
                }
            }
        }
    }
    private static void Continuity(BsonDocument previous, BsonDocument current)
    {
        if (Time(previous["suspendedAt"]) > Time(current["suspendedAt"]) || previous["poolRevision"].AsInt64 > current["poolRevision"].AsInt64)
            throw Invalid("suspension chronology");
        var mappings = current["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument)
            .ToDictionary(value => value["requestedLocation"]["channelId"].AsInt64);
        foreach (var value in previous["configuration"]["mappings"].AsBsonArray)
            if (!mappings.TryGetValue(value["requestedLocation"]["channelId"].AsInt64, out var mapping) ||
                mapping["aliasRevision"].AsInt64 < value["aliasRevision"].AsInt64) throw Invalid("lost historical channel");
        var stopped = previous["configuration"].DeepClone().AsBsonDocument; stopped["source"]["active"] = false;
        if (stopped.Equals(current["configuration"])) throw Invalid("duplicate unchanged stop");
    }
    private static void Audit(BsonDocument audit, BsonDocument row)
    {
        StockSourceSuspensionBson.Exact(audit, "_id", "schemaVersion", "kind", "operation", "actorId", "actorGrantVersion", "createdAt", "record");
        if (!StockSourceSuspensionBson.Positive(audit, "_id") || !StockSourceSuspensionBson.Positive(audit, "actorId") || audit["_id"] != row["auditId"] || !audit["schemaVersion"].IsInt32 || audit["schemaVersion"] != 1 ||
            audit["kind"] != "stock.source.suspension.audit" || audit["operation"] != Operation || audit["actorId"] != row["suspendedBy"] ||
            !StockSourceSuspensionBson.Positive(audit, "actorGrantVersion") || audit["createdAt"] != row["suspendedAt"] || !audit["record"].Equals(row)) throw Invalid("suspension audit provenance");
    }
    private static void Receipt(BsonDocument receipt, BsonDocument row)
    {
        StockSourceSuspensionBson.Exact(receipt, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "result", "createdAt");
        var poolId = row["poolId"].AsInt64; var expected = StockSourceSuspensionBson.Expectation(row);
        if (!receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 || !receipt["actorId"].IsInt64 ||
            receipt["actorId"] != row["suspendedBy"] || receipt["operation"] != Operation || !StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) ||
            receipt["bodyHash"] != AuthorizationStore.CommandHash(new { poolId, expected }, row["reason"].AsString) ||
            !receipt["result"].Equals(StockSourceSuspensionBson.Result(row)) || Time(receipt["createdAt"]) > Time(row["suspendedAt"])) throw Invalid("suspension receipt provenance");
    }
    private static DateTimeOffset Time(BsonValue value) => StockSourceSetupBson.Time(value);
    private static InvalidOperationException Invalid(string reason) => StockSourceSuspensionMigration.Invalid(reason);
}
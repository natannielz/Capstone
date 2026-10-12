using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal sealed record InventoryCountCoverageSnapshot(BsonDocument Head, BsonDocument[] Members, BsonDocument[] Observations);

/// <summary>Exhaustive current count-owner membership, including original unbound counts.
/// A membership authenticates an observation; it never closes or adjusts that observation.</summary>
internal sealed class InventoryCountCoverage(IMongoDatabase database)
{
    internal const string Heads = "inventoryCountCoverageHeads", Members = "inventoryCountCoverageMembers";
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static string Digest(BsonDocument row) => CatalogRepository.Digest(row);
    internal static string MembershipHash(IEnumerable<BsonDocument> rows) => Digest(new BsonDocument("members",
        new BsonArray(rows.OrderBy(row => row["_id"].AsInt64))));
    internal static BsonDocument Member(BsonDocument observation, string origin)
    {
        var count = StockCountBson.Read(observation);
        if (origin is not ("retained" or "live") || count.Location.OrganizationId is not { } organizationId) throw InventoryCountStockSourceRecords.Unavailable();
        var row = new BsonDocument { { "_id", count.Id }, { "schemaVersion", 1 }, { "kind", "inventory.countcoverage.member" },
            { "observationId", count.Id }, { "organizationId", organizationId }, { "location", observation["location"].DeepClone() },
            { "poolId", count.PoolBinding is { } binding ? new BsonInt64(binding.PoolId) : BsonNull.Value },
            { "origin", origin }, { "observationHash", Digest(observation) }, { "observationFingerprint", observation["fingerprint"] },
            { "observationBodyHash", observation["bodyHash"] }, { "observationAuditId", observation["auditId"] } };
        row.Add("memberHash", InventoryCountStockSourceRecords.Hash(row, "memberHash")); return row;
    }
    internal static BsonDocument Head(long organizationId, IEnumerable<BsonDocument> members)
    {
        var values = members.ToArray();
        return new() { { "_id", organizationId }, { "schemaVersion", 1 }, { "kind", "inventory.countcoverage.head" },
            { "writerSet", "stock-count-v101" }, { "memberCount", (long)values.Length }, { "revision", checked((long)values.Length + 1) },
            { "membershipHash", MembershipHash(values) }, { "fence", 0L } };
    }
    internal static async Task AuthenticateAsync(IMongoDatabase database, IClientSessionHandle tx, StockCountService counts,
        StockSourceSetupService sources, IEnumerable<BsonDocument> observations, CancellationToken ct, Func<CancellationToken, Task>? renew = null)
    {
        var checkedPools = new HashSet<long>(); var records = new InventoryCountStockSourceRecords(database);
        foreach (var observation in observations) {
            if (renew is not null) await renew(ct);
            var count = await counts.GraphObservationAsync(tx, observation, ct);
            if (count.PoolBinding is { } binding && checkedPools.Add(binding.PoolId))
                await records.RequireObservationAsync(tx, counts, sources, observation, ct);
        }
    }
    private async Task<BsonDocument[]> BoundedAsync(string name, BsonDocument filter, IClientSessionHandle tx, CancellationToken ct)
    {
        // ponytail: coverage admission is bounded at 4096 observations per organization;
        // use paged authenticated membership if measured legitimate volume reaches this ceiling.
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        return rows.ToArray();
    }
    internal async Task<InventoryCountCoverageSnapshot> ReadAsync(IClientSessionHandle tx, StockCountService counts,
        StockSourceSetupService sources, long organizationId, CancellationToken ct, BsonDocument? pending = null)
    {
        new InventoryRepository(database).RequireSession(tx); await InventoryCountStockSourceStorage.RequireCoverageAsync(database, ct);
        var observations = await BoundedAsync(StockCountBson.Collection, new BsonDocument("location.organizationId", organizationId), tx, ct);
        var existing = observations.Where(row => pending is null || row["_id"] != pending["_id"]).ToArray();
        if (pending is not null && (observations.Count(row => row == pending) != 1 || pending["location"]["organizationId"] != organizationId))
            throw InventoryCountStockSourceRecords.Unavailable();
        var ids = new BsonArray(existing.Select(row => row["_id"]));
        var members = await BoundedAsync(Members, new BsonDocument("$or", new BsonArray {
            new BsonDocument("organizationId", organizationId), new BsonDocument("observationId", new BsonDocument("$in", ids)) }), tx, ct);
        if (members.Length != existing.Length || !members.Select(row => row["_id"].AsInt64).ToHashSet().SetEquals(existing.Select(row => row["_id"].AsInt64)))
            throw InventoryCountStockSourceRecords.Unavailable();
        foreach (var member in members) {
            var observation = existing.Single(row => row["_id"] == member["_id"]);
            if (member != Member(observation, member["origin"].AsString)) throw InventoryCountStockSourceRecords.Unavailable();
        }
        await AuthenticateAsync(database, tx, counts, sources, existing, ct);
        var head = await Rows(Heads).Find(tx, new BsonDocument { { "_id", organizationId }, { "$and", new BsonArray {
            InventoryCountCoverageSchemas.Validator(Heads) } } }).SingleOrDefaultAsync(ct) ?? throw InventoryCountStockSourceRecords.Unavailable();
        if (head.ElementCount != 8 || head["memberCount"] != (long)members.Length || head["revision"] != checked((long)members.Length + 1) ||
            head["membershipHash"] != MembershipHash(members)) throw InventoryCountStockSourceRecords.Unavailable();
        return new(head, members, existing);
    }
    internal async Task InitializeFreshAsync(IClientSessionHandle tx, StockCountService counts, StockSourceSetupService sources,
        long organizationId, CancellationToken ct)
    {
        await InventoryCountStockSourceStorage.RequireCoverageAsync(database, ct); new InventoryRepository(database).RequireSession(tx);
        if (!await Rows(Heads).Find(tx, new BsonDocument("_id", organizationId)).AnyAsync(ct)) {
            if (await Rows(StockCountBson.Collection).Find(tx, new BsonDocument("location.organizationId", organizationId)).AnyAsync(ct) ||
                await Rows(Members).Find(tx, new BsonDocument("organizationId", organizationId)).AnyAsync(ct)) throw InventoryCountStockSourceRecords.Unavailable();
            await Rows(Heads).InsertOneAsync(tx, Head(organizationId, []), cancellationToken: ct);
        }
        _ = await ReadAsync(tx, counts, sources, organizationId, ct);
    }
    internal async Task AppendFreshAsync(IClientSessionHandle tx, StockCountService counts, StockSourceSetupService sources,
        BsonDocument observation, CancellationToken ct)
    {
        var organizationId = observation["location"]["organizationId"].AsInt64;
        var snapshot = await ReadAsync(tx, counts, sources, organizationId, ct, observation);
        if (snapshot.Head["fence"].AsInt64 >= long.MaxValue - 2) throw InventoryCountStockSourceRecords.Unavailable();
        await AuthenticateAsync(database, tx, counts, sources, [observation], ct);
        var member = Member(observation, "live"); await Rows(Members).InsertOneAsync(tx, member, cancellationToken: ct);
        if ((await Rows(Heads).UpdateOneAsync(tx, snapshot.Head, Builders<BsonDocument>.Update.Inc("memberCount", 1L).Inc("revision", 1L)
            .Inc("fence", 1L).Set("membershipHash", MembershipHash([.. snapshot.Members, member])), cancellationToken: ct)).ModifiedCount != 1)
            throw InventoryCountStockSourceRecords.Changed();
    }
    internal async Task FenceAsync(IClientSessionHandle tx, InventoryCountCoverageSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Head["fence"].AsInt64 >= long.MaxValue - 2) throw InventoryCountStockSourceRecords.Unavailable();
        if ((await Rows(Heads).UpdateOneAsync(tx, snapshot.Head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
            throw InventoryCountStockSourceRecords.Changed();
    }
    internal async Task RequireFreshUnboundAsync(IClientSessionHandle tx, StockCountService counts, StockSourceSetupService sources,
        GrantScope location, CancellationToken ct)
    {
        var stockLocation = StockSourceSetupService.StockCountLocation(location);
        await new StockSourceTopologyFence(database).FenceAsync(tx, stockLocation.OrganizationId.Value, ct);
        await InitializeFreshAsync(tx, counts, sources, stockLocation.OrganizationId.Value, ct);
        var target = StockSourceSetupBson.Location(stockLocation);
        // These are refusal signals, never an inferred source binding. Imported pools without
        // an owning setup registration remain neutral; corrupt relevant owned topology refuses.
        var matching = await Rows("inventoryPools").Find(tx, new BsonDocument("location", target)).Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (matching.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        foreach (var pool in matching)
            if (await Rows("inventoryPoolRegistrations").Find(tx, new BsonDocument("_id", pool["_id"])).AnyAsync(ct) ||
                await Rows("stockSourceDecisions").Find(tx, new BsonDocument { { "poolId", pool["_id"] }, { "decision", "approve" } }).AnyAsync(ct))
                throw InventoryCountStockSourceRecords.Unavailable();
        if (await Rows("stockSourceAliases").Find(tx, new BsonDocument { { "requestedLocation", target }, { "enabled", true } }).AnyAsync(ct))
            throw InventoryCountStockSourceRecords.Unavailable();
        var proposals = await Rows("stockSourceProposals").Find(tx, new BsonDocument("input.location", target))
            .Project(new BsonDocument("_id", 1)).Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (proposals.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        if (await Rows("stockSourceDecisions").Find(tx, new BsonDocument { { "proposalId", new BsonDocument("$in", new BsonArray(proposals.Select(row => row["_id"]))) },
            { "decision", "approve" } }).AnyAsync(ct)) throw InventoryCountStockSourceRecords.Unavailable();
    }
    internal async Task<(bool Complete, long OpenCount)> ForPoolAsync(IClientSessionHandle tx, InventoryCountCoverageSnapshot snapshot,
        InventoryCountSourceFacts facts, long boundOpenCount, CancellationToken ct)
    {
        var locations = new List<BsonDocument> { facts.Pool["location"].AsBsonDocument };
        locations.AddRange(facts.Configuration["mappings"].AsBsonArray.Select(value => value["requestedLocation"].AsBsonDocument));
        // Disabled and superseded mappings cannot hide earlier unbound physical work.
        var mappings = await Rows("stockSourceAliasDecisions").Find(tx, new BsonDocument { { "poolId", facts.Binding.PoolId }, { "decision", "approve" } })
            .Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (mappings.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        foreach (var mapping in mappings) {
            var proposal = await Rows("stockSourceAliasProposals").Find(tx, new BsonDocument("_id", mapping["proposalId"])).SingleOrDefaultAsync(ct);
            if (proposal is null) throw InventoryCountStockSourceRecords.Unavailable();
            StockSourceAliasBson.Decision(mapping, proposal);
            if (!proposal.TryGetValue("requestedLocation", out var location) || !location.IsBsonDocument) throw InventoryCountStockSourceRecords.Unavailable();
            locations.Add(location.AsBsonDocument);
        }
        var unbound = snapshot.Observations.Count(row => StockCountBson.Read(row).PoolBinding is null && locations.Any(location =>
            location == StockSourceSetupBson.Location(StockSourceSetupService.StockCountLocation(StockCountBson.Scope(row["location"].AsBsonDocument)))));
        // All current physical writers belong to their owning business-domain readers. Unknown
        // inventory-operation movement owners are unresolved work, never silently excluded.
        var unknown = await Rows("movements").Find(tx, new BsonDocument { { "physicalPoolId", facts.Binding.PoolId },
            { "source.namespace", new BsonDocument("$nin", new BsonArray { "ordering.order", "ordering.decision", "ordering.substitution", "fulfillment.shipment", UnitToko.Platform.Procurement.ProcurementStockReceiptBson.Operation, StockReleaseBson.Namespace }) } })
            .Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (unknown.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        return (unbound == 0 && unknown.Count == 0, checked(boundOpenCount + unbound + unknown.Count));
    }
}

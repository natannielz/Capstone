using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;
/// <summary>Additive causal coverage. Retained case/invoice/custody rows and their original
/// protocols are never rewritten. Unattributable legacy organizations retain blocked admission.</summary>
internal sealed class AfterSalesCausalCoverageMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 99;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("AfterSales causal coverage requires authentic retained causes and safe additive owners");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying" || database.Client is not MongoClient client || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Invalid();
        await AfterSalesStockSourceStorage.RequireAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
        var retained = (await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(row => row.GetValue("type", "") == "collection" && !AfterSalesCausalCoverageSchemas.Collections.Contains(row["name"].AsString)).ToArray();
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var definition in retained) indexes.Add(definition["name"].AsString,
            (await (await Rows(definition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var counter in counters) if (counter.ElementCount != 2 || !counter.GetValue("_id", BsonNull.Value).IsString ||
            string.IsNullOrWhiteSpace(counter["_id"].AsString) || !counter.GetValue("value", BsonNull.Value).IsInt64 ||
            counter["value"].AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        var candidates = new List<(string Name, bool Exists, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in AfterSalesCausalCoverageSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct); var required = AfterSalesCausalCoverageSchemas.Keys(name);
            var existing = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, AfterSalesCausalCoverageSchemas.Validator(name)) ||
                !OrderStockSourceStorage.SafeIndexes(existing, required)) || await Rows(name).Find(new BsonDocument("$nor",
                    new BsonArray { AfterSalesCausalCoverageSchemas.Validator(name) })).AnyAsync(ct)) throw Invalid();
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(ct);
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (name == AfterSalesStockSourceRecords.CausalHeads && counter is not null ||
                name == AfterSalesStockSourceRecords.CausalTransitions && (maximum is null ? counter is not null && counter["value"] != 0L :
                    counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Invalid();
            candidates.Add((name, definition is not null, required.Where(key => !existing.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        var owner = new AfterSalesStockSourceEvents(database, new(database), clock); var records = new AfterSalesStockSourceRecords(database);
        var setups = new List<AfterSalesStockSourceEvent>(); var causes = new List<AfterSalesStockSourceEvent>(); var blocked = new HashSet<long>();
        using (var tx = await client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
            await owner.OrphansAsync(tx, ct);
            var heads = await Rows(AfterSalesStockSourceRecords.Heads).Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            foreach (var head in heads) {
                await lease.RenewAsync(ct);
                var physical = await records.HistoryAsync(tx, owner, head["_id"].AsInt64, ct);
                await records.RequireSourcesAsync(tx, owner, head["_id"].AsInt64, physical.History, null, ct);
                var fulfillment = new FulfillmentStockSourceRecords(database);
                var dispatch = await fulfillment.HistoryAsync(tx, new FulfillmentStockSourceEvents(database, new(database), clock), head["_id"].AsInt64, ct);
                _ = await fulfillment.RequireSourcesAsync(tx, head["_id"].AsInt64, dispatch.History, null, ct);
                setups.Add(await owner.ReadCausalAsync(tx, "setup", head["setupDecisionId"].AsInt64, ct));
            }
            var configured = setups.Select(setup => setup.PoolIds.Single()).ToHashSet();
            var configuredOrganizations = setups.Select(setup => setup.Record["proposal"]["input"]["location"]["organizationId"].AsInt64).ToHashSet();
            var cases = await Rows(AfterSalesCaseRecords.Collection).Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            foreach (var candidate in cases) {
                await lease.RenewAsync(ct);
                var root = await new AfterSalesCaseRecords(database).ReadAsync(tx, candidate["_id"].AsInt64, ct);
                var decision = await new AfterSalesDecisionRecords(database).ReadAsync(tx, root, ct);
                var organizations = PaymentDraftBson.Scopes(root["source"]["scopes"].AsBsonArray).Select(scope => scope.FulfillmentOrganizationId).Distinct().ToArray();
                if (organizations.Length == 0 || organizations.Any(id => id is not > 0)) throw Invalid();
                var invoice = await Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", root["source"]["invoiceId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
                await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct);
                if (!InvoiceRecords.Equal(InvoiceService.CaseSnapshot(invoice), root["source"])) throw Invalid();
                // Unconfigured historical business remains readable through its original owners.
                // It gains no invented canonical pool or a successful zero-obligation assertion.
                if (!organizations.Any(id => configuredOrganizations.Contains(id!.Value))) continue;
                try {
                    var request = await owner.ReadCausalAsync(tx, "request", root["_id"].AsInt64, ct);
                    if (request.PoolIds.Any(pool => !configured.Contains(pool))) {
                        foreach (var id in organizations) blocked.Add(id!.Value);
                        continue;
                    }
                    causes.Add(request);
                    if (decision is not null) causes.Add(await owner.ReadCausalAsync(tx, "decision", root["_id"].AsInt64, ct));
                } catch (DomainValidationException error) when (error.Code is "fulfillment_source_obligation_unavailable" or "order_source_obligation_unavailable" or "aftersales_source_obligation_unavailable") {
                    foreach (var id in organizations) blocked.Add(id!.Value);
                }
            }
            await tx.CommitTransactionAsync(ct);
        }
        setups.RemoveAll(setup => blocked.Contains(setup.Record["proposal"]["input"]["location"]["organizationId"].AsInt64));
        var expectedPools = setups.Select(setup => setup.PoolIds.Single()).ToHashSet();
        causes = causes.Where(cause => cause.PoolIds.All(expectedPools.Contains)).OrderBy(cause => cause.Time)
            .ThenBy(cause => cause.Kind == "request" ? 0 : 1).ThenBy(cause => cause.Id).ToList();
        foreach (var setup in setups) {
            var pool = setup.PoolIds.Single(); var poolCauses = causes.Where(cause => cause.PoolIds.Contains(pool)).ToArray();
            if (poolCauses.Length + 1 > AfterSalesStockSourceRecords.MaximumRows || poolCauses.Any(cause => cause.Time.CompareTo(setup.Time) < 0 ||
                StockSourceSetupBson.Time(cause.Time) > clock.UtcNow)) throw Invalid();
        }
        var resumed = await Rows(AfterSalesStockSourceRecords.CausalHeads).Find(new BsonDocument()).AnyAsync(ct) ||
            await Rows(AfterSalesStockSourceRecords.CausalTransitions).Find(new BsonDocument()).AnyAsync(ct);
        if (resumed) await RequireSeededAsync(client, owner, records, expectedPools, ct);
        // All retained preflight completes before any additive DDL.
        foreach (var candidate in candidates) {
            await lease.RenewAsync(ct);
            if (!candidate.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", candidate.Name },
                { "validator", AfterSalesCausalCoverageSchemas.Validator(candidate.Name) }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in candidate.Missing) await Rows(candidate.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        if (!resumed) using (var tx = await client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            var producer = new AfterSalesStockSourceProducer(database, new(database), clock);
            foreach (var setup in setups) {
                await lease.RenewAsync(ct); await producer.InitializeCausalAsync(tx, setup, ct);
                var poolId = setup.PoolIds.Single();
                var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", poolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
                foreach (var cause in causes.Where(cause => cause.PoolIds.Contains(poolId))) {
                    var head = await Rows(AfterSalesStockSourceRecords.CausalHeads).Find(tx, new BsonDocument("_id", poolId)).SingleAsync(ct);
                    var previous = await Rows(AfterSalesStockSourceRecords.CausalTransitions).Find(tx, new BsonDocument("_id", head["lastTransitionId"])).SingleAsync(ct);
                    await producer.AppendCausalAsync(tx, cause, poolId, head, previous, "retained", pool!["revision"].AsInt64, pool["fence"].AsInt64, pool["fence"].AsInt64, ct);
                }
            }
            await tx.CommitTransactionAsync(ct);
        }
        await RequireSeededAsync(client, owner, records, expectedPools, ct);
        foreach (var before in retained) {
            var name = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before.GetValue("options", new BsonDocument()) != after.GetValue("options", new BsonDocument())) throw Invalid();
            var actual = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (actual.Count != indexes[name].Length || indexes[name].Any(row => !actual.Any(other => other == row))) throw Invalid();
        }
        var actualCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Where(row => row["_id"] != AfterSalesStockSourceRecords.CausalTransitions).Any(before =>
                !actualCounters.Any(after => after == before)) || actualCounters.Any(row => row["_id"] != AfterSalesStockSourceRecords.CausalTransitions && !counters.Any(before => before == row))) throw Invalid();
        await AfterSalesStockSourceStorage.RequireCoverageAsync(database, ct, ShipmentDispatchSchemas.LegacyValidator());
    }
    private async Task RequireSeededAsync(MongoClient client, AfterSalesStockSourceEvents owner, AfterSalesStockSourceRecords records,
        HashSet<long> expectedPools, CancellationToken ct)
    {
        using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
        var heads = await Rows(AfterSalesStockSourceRecords.CausalHeads).Find(tx, new BsonDocument()).ToListAsync(ct);
        if (!heads.Select(head => head["_id"].AsInt64).ToHashSet().SetEquals(expectedPools)) throw Invalid();
        var total = 0L;
        foreach (var head in heads) {
            var causal = await records.CausalHistoryAsync(tx, owner, head["_id"].AsInt64, ct);
            if (causal.History.Any(row => row["origin"] == "live")) throw Invalid();
            await records.RequireCausalSourcesAsync(tx, owner, head["_id"].AsInt64, causal.History, null, ct);
            total = checked(total + causal.History.Length);
        }
        if (await Rows(AfterSalesStockSourceRecords.CausalTransitions).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != total) throw Invalid();
        await tx.CommitTransactionAsync(ct);
    }
}

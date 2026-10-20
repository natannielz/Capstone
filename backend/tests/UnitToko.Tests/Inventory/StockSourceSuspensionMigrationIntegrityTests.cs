using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSuspensionMigrationTests
{
    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("event")] [InlineData("audit")] [InlineData("receipt")] [InlineData("actor")]
    [InlineData("audit-copy")] [InlineData("receipt-hash")] [InlineData("receipt-actor")]
    [InlineData("receipt-pool")] [InlineData("receipt-operation")] [InlineData("receipt-extra-operation")]
    [InlineData("duplicate-receipt")] [InlineData("duplicate-audit")] [InlineData("audit-operation")]
    [InlineData("hash")] [InlineData("source-snapshot")] [InlineData("alias-version")]
    [InlineData("original-receipt")] [InlineData("alias-receipt")] [InlineData("revived-pool")]
    [InlineData("suspension-counter")] [InlineData("original-counter")] [InlineData("numeric-type")]
    [InlineData("unique")] [InlineData("ttl")] [InlineData("lookup-hidden")] [InlineData("lookup-name")]
    public async Task Invalid_history_or_storage_is_rejected_before_any_target_or_counter_write(string fault)
    {
        await using var data = await StoreTestData.CreateForStockSourceSuspensionMigrationAsync(fixture);
        await InstallDraft(data); await Seed(data);
        var row = await Rows(data, Collection).Find(new BsonDocument()).SingleAsync();
        switch (fault)
        {
            case "event": await Rows(data, Collection).DeleteManyAsync(new BsonDocument()); break;
            case "audit": await Rows(data, "auditEvents").DeleteOneAsync(new BsonDocument("_id", row["auditId"])); break;
            case "receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.suspend")); break;
            case "actor": await Rows(data, "identities").DeleteOneAsync(new BsonDocument("_id", row["suspendedBy"])); break;
            case "audit-copy": await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]), Builders<BsonDocument>.Update.Set("record.reason", "Unexplained change")); break;
            case "audit-operation": await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]), Builders<BsonDocument>.Update.Set("operation", "stock.source.approve")); break;
            case "receipt-hash": await ReceiptChange("bodyHash", new string('a', 64)); break;
            case "receipt-actor": await ReceiptChange("actorId", 1000L); break;
            case "receipt-pool": await ReceiptChange("result.poolId", 99L); break;
            case "receipt-operation": await ReceiptChange("operation", "stock.source.approve"); break;
            case "duplicate-receipt": case "receipt-extra-operation":
                var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument("operation", "stock.source.suspend")).SingleAsync();
                receipt["_id"] = ObjectId.GenerateNewId(); receipt["key"] = "extra";
                if (fault == "receipt-extra-operation") receipt["operation"] = "stock.source.suspend.unknown";
                await Rows(data, "idempotencyRecords").InsertOneAsync(receipt); break;
            case "duplicate-audit":
                var audit = await Rows(data, "auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
                audit["_id"] = 9999L; await Rows(data, "auditEvents").InsertOneAsync(audit); break;
            case "hash": row["configurationHash"] = new string('a', 64); await ReplaceCoherently(data, row, false); break;
            case "source-snapshot": row["configuration"]["source"]["input"]["label"] = "Forged source"; await ReplaceCoherently(data, row); break;
            case "alias-version": row["configuration"]["mappings"][0]["aliasRevision"] = 2L; await ReplaceCoherently(data, row); break;
            case "original-receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.approve")); break;
            case "alias-receipt": await Rows(data, "idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "stock.source.alias.approve")); break;
            case "revived-pool": await Rows(data, "inventoryPools").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", true)); break;
            case "suspension-counter": await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", Collection), Builders<BsonDocument>.Update.Set("value", long.MaxValue)); break;
            case "original-counter": await Rows(data, "idCounters").UpdateOneAsync(new BsonDocument("_id", "inventoryPools"), Builders<BsonDocument>.Update.Set("value", 1.5), new UpdateOptions { BypassDocumentValidation = true }); break;
            case "numeric-type":
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Collection }, { "validator", new BsonDocument() } });
                await Rows(data, Collection).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("poolId", new BsonInt32(1))); break;
            case "unique": await Rows(data, Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("poolId", 1), new CreateIndexOptions { Name = "unexpected_unique", Unique = true })); break;
            case "ttl": await Rows(data, Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("suspendedAt", 1), new CreateIndexOptions { Name = "unexpected_ttl", ExpireAfter = TimeSpan.FromDays(10000) })); break;
            case "lookup-hidden": case "lookup-name":
                var index = StockSourceSuspensionSchemas.LookupIndexes()[0];
                await Rows(data, index.Collection).Indexes.DropOneAsync(index.Name);
                await Rows(data, index.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(fault == "lookup-name" ? new BsonDocument("wrong", 1) : index.Key,
                    new CreateIndexOptions { Name = index.Name, Hidden = fault == "lookup-hidden" })); break;
        }
        await Reject(data);
        Task ReceiptChange(string field, BsonValue value) => Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "stock.source.suspend"), Builders<BsonDocument>.Update.Set(field, value));
    }
    private static async Task ReplaceCoherently(StoreTestData data, BsonDocument row, bool hash = true)
    {
        if (hash) row["configurationHash"] = StockSourceSuspensionBson.Hash(row["configuration"].AsBsonDocument);
        await Rows(data, Collection).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("_id", row["auditId"]), Builders<BsonDocument>.Update.Set("record", row).Set("createdAt", row["suspendedAt"]));
        var poolId = row["poolId"].AsInt64; var expected = StockSourceSuspensionBson.Expectation(row);
        await Rows(data, "idempotencyRecords").UpdateOneAsync(new BsonDocument { { "operation", "stock.source.suspend" }, { "result.suspensionId", row["_id"] } },
            Builders<BsonDocument>.Update.Set("bodyHash", AuthorizationStore.CommandHash(new { poolId, expected }, row["reason"].AsString)).Set("result", StockSourceSuspensionBson.Result(row)));
    }
    private CollectionMigrations Observed(StoreTestData data, List<BsonDocument> writes)
    {
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "SuspensionMigration-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName is "create" or "collMod" or "createIndexes" or "dropIndexes" || e.CommandName == "update" && e.Command.GetValue("update", "") == "idCounters")
                lock (writes) writes.Add(e.Command.DeepClone().AsBsonDocument);
        });
        return new(new MongoClient(settings).GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock);
    }
    private async Task Reject(StoreTestData data)
    {
        var business = await Business(data); var history = await History(data);
        var counters = new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var writes = new List<BsonDocument>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Observed(data, writes).ApplyAsync());
        Assert.Empty(writes); Assert.Equal(business, await Business(data));
        Assert.Equal(counters, new BsonArray(await Rows(data, "idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var after = await History(data); Assert.Equal(history, new BsonArray(after.Take(17))); Assert.Equal("applying", after[17]["state"].AsString);
        Assert.False(await new CollectionMigrations(data.Database, data.Clock).IsCurrentAsync());
    }
}
using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class PurchaseOrderSourceReadCaptureTests(MongoFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Collection = "capturedOwnerRows";
    private static BsonDocument Schema => new("$jsonSchema", new BsonDocument {
        { "bsonType", "object" }, { "required", new BsonArray { "_id", "value", "marker", "recordedAt" } }, { "additionalProperties", false },
        { "properties", new BsonDocument { { "_id", new BsonDocument("bsonType", "long") },
            { "value", new BsonDocument("bsonType", "long") }, { "marker", new BsonDocument("bsonType", "string") },
            { "recordedAt", new BsonDocument("bsonType", "date") } } }
    });
    private static BsonDocument Row(long id = 1) => new() { { "_id", id }, { "value", 12L }, { "marker", "approved" },
        { "recordedAt", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc) } };
    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));
    private static bool HasSchema(BsonValue value) => value is BsonDocument document
        ? document.Contains("$jsonSchema") || document.Values.Any(HasSchema)
        : value is BsonArray array && array.Any(HasSchema);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Successful_exact_id_schema_reads_replay_actual_bytes_without_replanning_schema_and_refuse_warmed_corruption(int shape)
    {
        var name = fixture.DatabaseName + "_proof_" + Guid.NewGuid().ToString("N")[..8];
        var observed = new ConcurrentQueue<BsonDocument>();
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "CapturedOwnerReplay-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == name && command.Command.GetValue("find", BsonNull.Value) == Collection)
                observed.Enqueue(command.Command["filter"].DeepClone().AsBsonDocument);
        });
        using var client = new MongoClient(settings); var database = client.GetDatabase(name); var rows = database.GetCollection<BsonDocument>(Collection);
        try {
            await rows.InsertManyAsync([Row(), new BsonDocument { { "_id", 2L }, { "value", "malformed" }, { "marker", "approved" } }]);
            foreach (var fault in shape < 2 ? new[] { "mutation", "deletion" } : new[] { "mutation", "deletion", "expression", "date" }) {
                using var tx = await client.StartSessionAsync(); tx.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
                try {
                    var capture = new PurchaseOrderSourceReadCapture();
                    // ReadReceiptAsync passes the third form directly as BSON. The
                    // fourth goes through the driver's flattening; both retain the
                    // expression/date predicates after the proven schema is removed.
                    var expression = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$value", 12L }));
                    var dates = new BsonDocument("recordedAt", new BsonDocument {
                        { "$gte", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                        { "$lt", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
                    });
                    var filter = shape switch {
                        0 => new BsonDocument { { "_id", 1L }, { "$and", new BsonArray { Schema } } },
                        1 => Render(Builders<BsonDocument>.Filter.Eq("_id", 1L) & Schema),
                        2 => new BsonDocument { { "_id", 1L }, { "$and", new BsonArray {
                            new BsonDocument("$and", new BsonArray { Schema, expression, dates }) } } },
                        _ => Render(Builders<BsonDocument>.Filter.Eq("_id", 1L) & new BsonDocument("$and", new BsonArray {
                            new BsonDocument("$and", new BsonArray { Schema, expression, dates }) }))
                    };
                    Assert.Equal(shape == 3 ? 4 : 2, filter.ElementCount);
                    if (shape == 3 && fault == "mutation") {
                        var actualReceiptFilter = Render(Builders<BsonDocument>.Filter.Eq("_id", 1L) &
                            UnitToko.Platform.Mongo.Migrations.ProcurementStockReceiptSchemas.Validator("procurementStockReceipts"));
                        output.WriteLine("ACTUAL_RECEIPT_FILTER_SHAPE " + string.Join(",", actualReceiptFilter.Names));
                    }
                    Assert.Equal(Row(), await capture.OneAsync(database, tx, Collection, filter, default));
                    Assert.True(HasSchema(observed.Last()));
                    // This second genuine read mirrors receipt genesis joining the same immutable owner.
                    if (shape < 2) Assert.Equal(Row(), await capture.OneAsync(database, tx, Collection, new BsonDocument("_id", 1L), default));
                    capture.Seal(); observed.Clear();
                    await capture.RequireAsync(database, tx, default);
                    await capture.RequireAsync(database, tx, default);
                    Assert.Equal(2, observed.Count);
                    Assert.All(observed, query => {
                        Assert.False(HasSchema(query));
                        if (shape < 2) Assert.Equal(new BsonDocument("_id", 1L), query);
                        else if (shape == 2) {
                            Assert.Contains(expression.ToJson(), query.ToJson(), StringComparison.Ordinal);
                            Assert.Contains(dates.ToJson(), query.ToJson(), StringComparison.Ordinal);
                        } else {
                            Assert.Equal(3, query.ElementCount);
                            Assert.Equal(filter["_id"], query["_id"]);
                            Assert.Equal(expression["$expr"], query["$expr"]);
                            Assert.Equal(dates["recordedAt"], query["recordedAt"]);
                        }
                    });
                    if (fault == "mutation") await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("value", "corrupt"));
                    else if (fault == "expression") await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("value", 13L));
                    else if (fault == "date") await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("recordedAt", new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
                    else await rows.DeleteOneAsync(tx, new BsonDocument("_id", 1L));
                    var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => capture.RequireAsync(database, tx, default));
                    Assert.Equal("procurement_quality_acceptance_integrity_unavailable", failure.Code);
                    Assert.Equal(Collection, failure.Data["purchaseOrderCapture.collection"]);
                } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
                Assert.Equal(Row(), await rows.Find(new BsonDocument("_id", 1L)).SingleAsync());
            }
            using var invalidTx = await client.StartSessionAsync(); invalidTx.StartTransaction();
            var invalid = new PurchaseOrderSourceReadCapture();
            Assert.Null(await invalid.OneAsync(database, invalidTx, Collection,
                Render(Builders<BsonDocument>.Filter.Eq("_id", 2L) & Schema), default));
            await invalidTx.AbortTransactionAsync();
        } finally { await client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Additional_business_predicates_and_negative_membership_remain_part_of_the_replayed_query()
    {
        var name = fixture.DatabaseName + "_proof_" + Guid.NewGuid().ToString("N")[..8];
        var database = fixture.Client.GetDatabase(name); var rows = database.GetCollection<BsonDocument>(Collection);
        try {
            await rows.InsertOneAsync(Row());
            using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            try {
                var absent = new PurchaseOrderSourceReadCapture();
                var filter = new BsonDocument { { "_id", 1L }, { "$and", new BsonArray { Schema, new BsonDocument("marker", "pending") } } };
                Assert.Null(await absent.OneAsync(database, tx, Collection, filter, default));
                absent.Seal();
                await absent.RequireAsync(database, tx, default);
                await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("marker", "pending"));
                Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                    (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => absent.RequireAsync(database, tx, default))).Code);
            } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
            Assert.Equal(Row(), await rows.Find(new BsonDocument("_id", 1L)).SingleAsync());
        } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
}

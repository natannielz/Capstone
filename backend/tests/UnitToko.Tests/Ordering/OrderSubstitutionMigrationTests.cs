using System.Runtime.Versioning;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Inventory;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_113_prefix_retained_order_corruption_refuses_then_interrupted_114_resumes_without_rewriting_history_or_creating_consent() {
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, converted: false); await using var data = prepared.Data;
        var history = await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        // This test requires the actual composed prefix. It never synthesizes applied migrations.
        Assert.Equal(CollectionMigrations.CurrentVersion, history.Count); Assert.True(history.Count >= 114);
        Assert.All(history, row => Assert.Equal("applied", row["state"].AsString));
        foreach (var name in OrderSubstitutionSchemas.Collections) {
            Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
            await data.Database.DropCollectionAsync(name);
        }
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", OrderStockSourceRecords.Transitions },
            { "validator", OrderStockSourceSchemas.Validator(OrderStockSourceRecords.Transitions) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        foreach (var name in OrderLineageSchemas.ChangedCollections)
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", OrderLineageSchemas.Previous(name) },
                { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows("reservations").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(OrderSubmissionSchemas.Key("reservations"),
            new CreateIndexOptions { Name = OrderLineageMigrationCapture.PreviousReservationIndex, Unique = true }));
        await data.Rows("reservations").Indexes.DropOneAsync(OrderLineageMigrationCapture.ReservationIndex);
        // Explicit historical migrations may inspect the retained original contract. Ordinary
        // current runtime must still refuse this downgraded receipt collection before114.
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() =>
            new UnitToko.Platform.Fulfillment.ShipmentReceiptRecords(data.Database).StorageAsync(default));
        // A stricter operator-owned conjunct must survive the forward schema extension.
        var extra = new BsonDocument("_id", new BsonDocument("$gt", 0L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "reservations" },
            { "validator", new BsonDocument("$and", new BsonArray { OrderSubmissionSchemas.Validator("reservations"), extra }) } });
        var retained = new Dictionary<string, BsonDocument>();
        var retainedRows = new Dictionary<string, BsonArray>();
        var retainedIndexes = new Dictionary<string, BsonArray>();
        foreach (var name in OrderLineageSchemas.ChangedCollections) {
            retained.Add(name, (await AccountContactStorage.DefinitionAsync(data.Database, name, default))!);
            retainedRows.Add(name, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            retainedIndexes.Add(name, new BsonArray(await (await data.Rows(name).Indexes.ListAsync()).ToListAsync()));
        }
        Assert.Equal(history.Count - 113L, (await data.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 114)))).DeletedCount);
        var prefix = new BsonArray(history.Take(113));
        var originalDefinition = await AccountContactStorage.DefinitionAsync(data.Database, OrderStockSourceRecords.Transitions, default);
        Assert.NotNull(originalDefinition);
        var originalIndexes = await (await data.Rows(OrderStockSourceRecords.Transitions).Indexes.ListAsync()).ToListAsync();
        var oldOrder = await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync();
        var gate = new MigrationGate(data.Database, data.Clock, 114);
        async Task RefuseWithoutDdl() {
            var held = await gate.AcquireAsync(114, default); Assert.NotNull(held);
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => new OrderSubstitutionMigration(data.Database, (MongoClient)data.Database.Client, data.Clock).ApplyAsync(held, default)); }
            finally { await held.ReleaseAsync(); }
            foreach (var name in OrderSubstitutionSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
            foreach (var (name, definition) in retained) Assert.Equal(definition, await AccountContactStorage.DefinitionAsync(data.Database, name, default));
            Assert.Equal(originalDefinition, await AccountContactStorage.DefinitionAsync(data.Database, OrderStockSourceRecords.Transitions, default));
            Assert.Equal(originalIndexes, await (await data.Rows(OrderStockSourceRecords.Transitions).Indexes.ListAsync()).ToListAsync());
            foreach (var (name, rows) in retainedRows)
                Assert.Equal(rows, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            foreach (var (name, indexes) in retainedIndexes)
                Assert.Equal(indexes, new BsonArray(await (await data.Rows(name).Indexes.ListAsync()).ToListAsync()));
            Assert.Equal(oldOrder, await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync());
        }
        foreach (var (name, field, marker) in new[] {
            ("auditEvents", "operation", OrderSubstitutionBson.ProposeOperation), ("auditEvents", "kind", "ordering.substitution.decided"),
            ("idempotencyRecords", "operation", OrderSubstitutionBson.AcceptOperation), ("outboxEvents", "kind", OrderSubstitutionBson.DeclineOperation),
            ("outboxEvents", "sourceKey", OrderSubstitutionBson.ProposeOperation + ":1") }) {
            // Start with a genuine retained row so refusal is specifically the new orphan marker.
            var original = await data.Rows(name).Find(new BsonDocument()).FirstAsync(); var orphan = original.DeepClone().AsBsonDocument;
            orphan["_id"] = original["_id"].IsObjectId ? (BsonValue)ObjectId.GenerateNewId() : 89000001L;
            if (orphan.Contains("requestKey")) orphan["requestKey"] = "r02-migration-orphan-" + field;
            if (orphan.Contains("key")) orphan["key"] = "r02-migration-orphan-" + field;
            if (orphan.Contains("sourceKey")) orphan["sourceKey"] = "fixture.r02-unrelated:1";
            orphan[field] = marker;
            await data.Rows(name).InsertOneAsync(orphan);
            try { await RefuseWithoutDdl(); Assert.Equal(orphan, await data.Rows(name).Find(new BsonDocument("_id", orphan["_id"])).SingleAsync()); }
            finally { Assert.Equal(1L, (await data.Rows(name).DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        }
        var malformedCounter = new BsonDocument { { "_id", OrderSubstitutionBson.Proposals }, { "value", 0L }, { "unrecognized", true } };
        await data.Rows("idCounters").InsertOneAsync(malformedCounter, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefuseWithoutDdl(); Assert.Equal(malformedCounter, await data.Rows("idCounters").Find(new BsonDocument("_id", OrderSubstitutionBson.Proposals)).SingleAsync()); }
        finally { await data.Rows("idCounters").DeleteOneAsync(new BsonDocument("_id", OrderSubstitutionBson.Proposals)); }
        var corrupt = oldOrder.DeepClone().AsBsonDocument; corrupt["snapshotHash"] = new string('a', 64);
        await data.Rows("orders").ReplaceOneAsync(new BsonDocument("_id", data.OrderId), corrupt);
        var lease = await gate.AcquireAsync(114, default); Assert.NotNull(lease);
        try {
            var denied = await Assert.ThrowsAnyAsync<Exception>(() => new OrderSubstitutionMigration(data.Database, (MongoClient)data.Database.Client, data.Clock).ApplyAsync(lease, default));
            Assert.True(denied is UnitToko.Modules.Contracts.DomainValidationException or UnitToko.Platform.Authorization.AuthorizationDeniedException or InvalidOperationException, denied.ToString());
        }
        finally { await lease.ReleaseAsync(); }
        foreach (var name in OrderSubstitutionSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
        foreach (var (name, definition) in retained) Assert.Equal(definition, await AccountContactStorage.DefinitionAsync(data.Database, name, default));
        Assert.Equal(originalDefinition, await AccountContactStorage.DefinitionAsync(data.Database, OrderStockSourceRecords.Transitions, default));
        Assert.Equal(corrupt, await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync());
        await data.Rows("orders").ReplaceOneAsync(new BsonDocument("_id", data.OrderId), oldOrder);
        // Reproduce an interrupted first DDL while retaining the real acquired114 history row.
        lease = await gate.AcquireAsync(114, default); Assert.NotNull(lease);
        await data.Database.CreateCollectionAsync(OrderSubstitutionBson.Proposals, new CreateCollectionOptions<BsonDocument> {
            Validator = OrderSubstitutionSchemas.Validator(OrderSubstitutionBson.Proposals), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        var preserved = await AccountContactStorage.DefinitionAsync(data.Database, OrderSubstitutionBson.Proposals, default); Assert.NotNull(preserved);
        // Resume also covers the safe intermediate state with both reservation indexes present.
        await data.Rows("reservations").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(OrderLineageSchemas.OrderKey("reservations"),
            new CreateIndexOptions { Name = OrderLineageMigrationCapture.ReservationIndex, Unique = true }));
        await lease.ReleaseAsync();
        lease = await gate.AcquireAsync(114, default); Assert.NotNull(lease);
        try { await new OrderSubstitutionMigration(data.Database, (MongoClient)data.Database.Client, data.Clock).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
        Assert.Equal(prefix, new BsonArray((await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Take(113)));
        Assert.Equal(oldOrder, await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync());
        Assert.Equal(preserved["info"]["uuid"], (await AccountContactStorage.DefinitionAsync(data.Database, OrderSubstitutionBson.Proposals, default))!["info"]["uuid"]);
        Assert.Equal(originalDefinition["info"]["uuid"], (await AccountContactStorage.DefinitionAsync(data.Database, OrderStockSourceRecords.Transitions, default))!["info"]["uuid"]);
        Assert.Equal(originalIndexes, await (await data.Rows(OrderStockSourceRecords.Transitions).Indexes.ListAsync()).ToListAsync());
        await OrderSubstitutionRecords.RequireStorageAsync(data.Database, default);
        foreach (var (name, before) in retained) {
            var after = await AccountContactStorage.DefinitionAsync(data.Database, name, default); Assert.NotNull(after);
            Assert.Equal(before["info"]["uuid"], after["info"]["uuid"]);
            Assert.True(AccountContactStorage.SafeDefinition(after, OrderLineageSchemas.Expand(OrderLineageSchemas.Previous(name))));
            Assert.Equal(retainedRows[name], new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        }
        var reservationDefinition = await AccountContactStorage.DefinitionAsync(data.Database, "reservations", default);
        Assert.Contains(extra, reservationDefinition!["options"]["validator"]["$and"].AsBsonArray);
        var reservationIndexes = await (await data.Rows("reservations").Indexes.ListAsync()).ToListAsync();
        Assert.DoesNotContain(reservationIndexes, row => row["name"] == OrderLineageMigrationCapture.PreviousReservationIndex);
        Assert.Single(reservationIndexes, row => row["name"] == OrderLineageMigrationCapture.ReservationIndex && row["key"] == OrderLineageSchemas.OrderKey("reservations") && row["unique"].AsBoolean);
        foreach (var name in OrderSubstitutionSchemas.Collections) Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Null(await gate.AcquireAsync(114, default));
    }
}

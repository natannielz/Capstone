using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementCorrectionMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_old_image_correction_survives_sole_stronger_extension_and_replay_while_malformed_orphan_annotated_and_history_faults_refuse_before_DDL()
    {
        // All successful rows come from actual admission, native image/PDF OCR and
        // the old public correction command, including its normal audit and receipt.
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture);
        var db = data.Data.Database; var command = PaymentSupplementCorrectionTestData.Meta("migration-legacy-image");
        var saved = await data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0,
            data.Review.Binding, data.Fields, command);
        var oldVersion = await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1, oldVersion["schemaVersion"].AsInt32); Assert.False(oldVersion.Contains("supplementaryObservations"));
        var oldAudit = await data.Rows("auditEvents").Find(new BsonDocument("_id", oldVersion["auditId"])).SingleAsync();
        var oldReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument {
            { "operation", PaymentCorrectionBson.Operation }, { "key", command.IdempotencyKey } }).SingleAsync();
        var oldOutbox = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", PaymentCorrectionBson.SourceKey(oldVersion))).SingleAsync();

        // Test-only predecessor rehearsal: remove only the proven-empty applied88
        // marker, then replace an EXACT reviewed successor with its exact predecessor.
        // No annotated writer, unknown template or existing operator rule is discarded.
        await PaymentSupplementMigrationTests.RestoreV87Async(db);
        var current = await DefinitionAsync(db, PaymentCorrectionBson.Versions);
        Assert.Equal(PaymentSupplementCorrectionSchemas.Validator(PaymentCorrectionBson.Versions), current["options"]["validator"]);
        var predecessor = new BsonDocument("$and", new BsonArray {
            PaymentCorrectionSchemas.Validator(PaymentCorrectionBson.Versions), new BsonDocument("_id", new BsonDocument("$ne", 999888L)) });
        Assert.False(await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("$nor", new BsonArray { predecessor })).AnyAsync());
        await ChangeAsync(db, predecessor);
        Assert.Equal(current["info"]["uuid"], (await DefinitionAsync(db, PaymentCorrectionBson.Versions))["info"]["uuid"]);
        await data.Rows(PaymentCorrectionBson.Versions).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "recordedBy", 1 }, { "recordedAt", -1 } }, new CreateIndexOptions { Name = "ix_operator_correction_actor" }));
        var history87 = await HistoryAsync(db); Assert.Equal(87, history87.Count);

        var malformed = oldVersion.DeepClone().AsBsonDocument; malformed["snapshotHash"] = new string('f', 64);
        await data.Rows(PaymentCorrectionBson.Versions).ReplaceOneAsync(new BsonDocument("_id", oldVersion["_id"]), malformed);
        try { await RefusesAsync(db, data.Data.Catalog.Clock, history87); }
        finally { await data.Rows(PaymentCorrectionBson.Versions).ReplaceOneAsync(new BsonDocument("_id", oldVersion["_id"]), oldVersion); }

        // Fault injection clones a genuine event, then deliberately removes its
        // owner relation. Its exact temporary ID alone is removed after refusal.
        var orphan = oldOutbox.DeepClone().AsBsonDocument; orphan["_id"] = ObjectId.GenerateNewId();
        orphan["sourceKey"] = PaymentCorrectionBson.Operation + ":999887"; orphan["payload"]["correctionId"] = 999887L;
        await data.Rows("outboxEvents").InsertOneAsync(orphan);
        try { await RefusesAsync(db, data.Data.Catalog.Clock, history87); }
        finally { Assert.Equal(1L, (await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }

        var annotated = oldVersion.DeepClone().AsBsonDocument; annotated["schemaVersion"] = 2;
        annotated["supplementaryObservations"] = PaymentCorrectionBson.Observations(ImmutableArray.Create(data.Observation()));
        annotated["snapshotHash"] = PaymentCorrectionBson.Hash(annotated);
        // The retained strict predecessor itself must reject this prospective writer.
        var schemaRefusal = await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(PaymentCorrectionBson.Versions)
            .ReplaceOneAsync(new BsonDocument("_id", oldVersion["_id"]), annotated));
        Assert.Equal(121, schemaRefusal.WriteError.Code);
        // Deliberate bypass is restricted to this negative test row; validators
        // remain strict so migration must refuse corrupt retained data before DDL.
        await data.Rows(PaymentCorrectionBson.Versions).ReplaceOneAsync(new BsonDocument("_id", oldVersion["_id"]), annotated,
            new ReplaceOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(db, data.Data.Catalog.Clock, history87); }
        finally { await data.Rows(PaymentCorrectionBson.Versions).ReplaceOneAsync(new BsonDocument("_id", oldVersion["_id"]), oldVersion); }

        var marker87 = history87[^1].AsBsonDocument; var invalidMarker = marker87.DeepClone().AsBsonDocument;
        invalidMarker["state"] = "applying";
        await data.Rows("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 87), invalidMarker);
        var invalidHistory = await HistoryAsync(db); var beforeInvalidHistory = await SnapshotAsync(db);
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentSupplementCorrectionTestData.ApplyAsync(db, data.Data.Catalog.Clock));
            Assert.Equal(HistoryBytes(invalidHistory), HistoryBytes(await HistoryAsync(db)));
            Assert.Equal(beforeInvalidHistory.ToBson(), (await SnapshotAsync(db)).ToBson());
        } finally { await data.Rows("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 87), marker87); }

        var before = await SnapshotAsync(db); await PaymentSupplementCorrectionTestData.ApplyAsync(db, data.Data.Catalog.Clock);
        var expected = before.DeepClone().AsBsonDocument;
        expected[PaymentCorrectionBson.Versions]["definition"]["options"]["validator"] = PaymentSupplementCorrectionSchemas.Extend(predecessor);
        Assert.Equal(expected.ToBson(), (await SnapshotAsync(db)).ToBson());
        Assert.Equal(HistoryBytes(history87), HistoryBytes(new BsonArray((await HistoryAsync(db)).Take(87))));
        Assert.Equal("applied", (await HistoryAsync(db))[^1]["state"]);
        var after = await SnapshotAsync(db); await PaymentSupplementCorrectionTestData.ApplyAsync(db, data.Data.Catalog.Clock);
        Assert.Equal(after.ToBson(), (await SnapshotAsync(db)).ToBson());
        Assert.Equal(2, (await DefinitionAsync(db, PaymentCorrectionBson.Versions))["options"]["validator"]["$and"].AsBsonArray.Count);

        // Normal command replay may fence current heads/allocate unused IDs; the
        // sole migration above already proved every counter and row unchanged.
        Assert.Equal(saved, await data.Restart().CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0, data.Review.Binding, data.Fields, command));
        Assert.Equal(oldVersion, await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(oldAudit, await data.Rows("auditEvents").Find(new BsonDocument("_id", oldAudit["_id"])).SingleAsync());
        Assert.Equal(oldReceipt, await data.Rows("idempotencyRecords").Find(new BsonDocument("_id", oldReceipt["_id"])).SingleAsync());
        Assert.Equal(oldOutbox, await data.Rows("outboxEvents").Find(new BsonDocument("_id", oldOutbox["_id"])).SingleAsync());
        await data.UnchangedAsync();
    }

    private static async Task RefusesAsync(IMongoDatabase db, IClock clock, BsonArray history87)
    {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentSupplementCorrectionTestData.ApplyAsync(db, clock));
        Assert.Equal("Payment supplementary correction migration found incompatible retained storage", error.Message);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
        var history = await HistoryAsync(db); Assert.Equal(88, history.Count);
        Assert.Equal(HistoryBytes(history87), HistoryBytes(new BsonArray(history.Take(87)))); Assert.Equal("applying", history[^1]["state"]);
    }
    private static Task<BsonDocument> ChangeAsync(IMongoDatabase db, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", PaymentCorrectionBson.Versions }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) =>
        new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static byte[] HistoryBytes(BsonArray history) => new BsonDocument("history", history).ToBson();
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var snapshot = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            snapshot[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return snapshot;
    }
}

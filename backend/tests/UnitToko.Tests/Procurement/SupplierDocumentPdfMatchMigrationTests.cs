using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class SupplierDocumentPdfMatchMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_plain_and_quality_match_history_survives_sole_validator_extension_and_replay_and_corrupt_orphan_provenance_and_history_refuse_before_DDL() {
        await using var data = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture, quality: true);
        var source = data.Data; var db = source.Database;
        var quality = await source.MatchAsync("retained-quality", data.PlainInput());
        var unresolved = data.PlainInput() with { Lines = [new(1, null, null, null, null, null, null, null, null, null,
            "A separate unresolved annotation makes no physical or monetary claim")] };
        var plain = await source.MatchAsync("retained-plain", unresolved);
        var legacy = await source.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(new[] { 2, 1 }, legacy.Select(row => row["schemaVersion"].AsInt32).ToArray());
        await SupplierDocumentPdfMatchMigrationRehearsal.RestoreV88Async(db);
        var installed = await (await db.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", SupplierDocumentBson.Matches) })).SingleAsync();
        Assert.Equal(SupplierDocumentPdfMatchSchemas.Validator(SupplierDocumentBson.Matches), installed["options"]["validator"]);
        var predecessor = new BsonDocument("$and", new BsonArray {
            ProcurementQualityValuationSchemas.PrePdfDocumentValidator(SupplierDocumentBson.Matches),
            new BsonDocument("_id", new BsonDocument("$ne", 998989L)) });
        Assert.False(await source.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument("$nor", new BsonArray { predecessor })).AnyAsync());
        await ChangeAsync(db, predecessor);
        var restored = await (await db.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", SupplierDocumentBson.Matches) })).SingleAsync();
        Assert.Equal(installed["info"]["uuid"], restored["info"]["uuid"]);
        await source.Rows(SupplierDocumentBson.Matches).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "actorId", 1 }, { "recordedAt", -1 } }, new CreateIndexOptions { Name = "ix_operator_pdf_match_actor" }));
        var history88 = await HistoryAsync(db);

        var malformed = legacy[0].DeepClone().AsBsonDocument; malformed["snapshotHash"] = new string('f', 64);
        await source.Rows(SupplierDocumentBson.Matches).ReplaceOneAsync(new BsonDocument("_id", malformed["_id"]), malformed);
        try { await RefusesAsync(db, source.Clock, history88); }
        finally { await source.Rows(SupplierDocumentBson.Matches).ReplaceOneAsync(new BsonDocument("_id", legacy[0]["_id"]), legacy[0]); }
        var eventRow = await source.Rows("outboxEvents").Find(new BsonDocument("sourceKey", SupplierDocumentBson.EffectKey(legacy[0]))).SingleAsync();
        var orphan = eventRow.DeepClone().AsBsonDocument; orphan["_id"] = ObjectId.GenerateNewId();
        orphan["sourceKey"] = SupplierDocumentTestData.MatchOperation + ":998988"; orphan["payload"]["matchId"] = 998988L;
        await source.Rows("outboxEvents").InsertOneAsync(orphan);
        try { await RefusesAsync(db, source.Clock, history88); }
        finally { Assert.Equal(1L, (await source.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        var prospective = legacy[0].DeepClone().AsBsonDocument; prospective["schemaVersion"] = 4;
        prospective["input"]["pdfObservations"] = SupplierDocumentBson.Input(data.AnnotatedInput())["pdfObservations"];
        prospective["snapshotHash"] = SupplierDocumentBson.Hash(prospective);
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => source.Rows(SupplierDocumentBson.Matches)
            .ReplaceOneAsync(new BsonDocument("_id", prospective["_id"]), prospective))).WriteError.Code);
        // Negative corruption only: the predecessor remains strict and no annotation is accepted.
        await source.Rows(SupplierDocumentBson.Matches).ReplaceOneAsync(new BsonDocument("_id", prospective["_id"]), prospective,
            new ReplaceOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(db, source.Clock, history88); }
        finally { await source.Rows(SupplierDocumentBson.Matches).ReplaceOneAsync(new BsonDocument("_id", legacy[0]["_id"]), legacy[0]); }
        var marker88 = history88[^1].AsBsonDocument; var bad = marker88.DeepClone().AsBsonDocument; bad["state"] = "applying";
        await source.Rows("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 88), bad);
        var invalid = await SnapshotAsync(db); var invalidHistory = await HistoryAsync(db);
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, source.Clock));
            Assert.Equal(invalid.ToBson(), (await SnapshotAsync(db)).ToBson()); Assert.Equal(invalidHistory, await HistoryAsync(db));
        } finally { await source.Rows("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 88), marker88); }

        var before = await SnapshotAsync(db); await ApplyAsync(db, source.Clock);
        var expected = before.DeepClone().AsBsonDocument;
        expected[SupplierDocumentBson.Matches]["definition"]["options"]["validator"] = SupplierDocumentPdfMatchSchemas.Extend(predecessor);
        Assert.Equal(expected.ToBson(), (await SnapshotAsync(db)).ToBson());
        Assert.Equal(history88, new BsonArray((await HistoryAsync(db)).Take(88)));
        var current = await SnapshotAsync(db); await ApplyAsync(db, source.Clock); Assert.Equal(current.ToBson(), (await SnapshotAsync(db)).ToBson());
        Assert.Equal(legacy, await source.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(quality, await source.Service.LookupAsync(source.Matcher, SupplierDocumentTestData.MatchOperation, "retained-quality"));
        Assert.Equal(plain, await source.Service.MatchAsync(source.Matcher, unresolved, legacy[1]["binding"].AsString, SupplierDocumentTestData.Meta("retained-plain")));
        await data.UnchangedAsync();
    }
    private static async Task ApplyAsync(IMongoDatabase db, IClock clock) {
        var lease = await new MigrationGate(db, clock, 89).AcquireAsync(89, default); if (lease is null) return;
        try { await new SupplierDocumentPdfMatchMigration(db, clock).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock, BsonArray history88) {
        var before = await SnapshotAsync(db); await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, clock));
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson()); var history = await HistoryAsync(db);
        Assert.Equal(89, history.Count); Assert.Equal(history88, new BsonArray(history.Take(88))); Assert.Equal("applying", history[^1]["state"]);
    }
    private static Task<BsonDocument> ChangeAsync(IMongoDatabase db, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(
        new BsonDocument { { "collMod", SupplierDocumentBson.Matches }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db) {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}

// Portable Mongo-only rehearsal. Native fixture creation remains on the Windows
// test class; this helper never opens an original, invokes a decoder or runs OCR.
internal static class SupplierDocumentPdfMatchMigrationRehearsal
{
    internal static async Task RestoreV88Async(IMongoDatabase db) {
        var history = new BsonArray(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument())
            .Sort(new BsonDocument("_id", 1)).ToListAsync());
        if (history.Count <= 88) return;
        Assert.Equal(89, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 89));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await SupplierDocumentPdfMatchStorage.RequireAsync(db, SupplierDocumentBson.Matches, default, requireAnnotated: true);
        Assert.False(await db.GetCollection<BsonDocument>(SupplierDocumentBson.Matches).Find(new BsonDocument("$or", new BsonArray {
            new BsonDocument("schemaVersion", new BsonDocument("$in", new BsonArray { 3, 4 })),
            new BsonDocument("input.pdfObservations", new BsonDocument("$exists", true)) })).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("auditEvents").Find(new BsonDocument("$or", new BsonArray {
            new BsonDocument("pdfObservations", new BsonDocument("$exists", true)),
            new BsonDocument("input.pdfObservations", new BsonDocument("$exists", true)) })).AnyAsync());
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument {
            { "_id", 89 }, { "state", "applied" } })).DeletedCount);
    }
}

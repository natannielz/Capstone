using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Operations;

namespace UnitToko.Tests.Operations;

// Q09 (Phase E task 6): a restore is accepted only when every collection (documents, validator, indexes) and every
// evidence object matches what was captured before the backup. A restore that "succeeds" with anything missing fails.
[Collection("Mongo")]
public sealed class RestoreDrillTests(MongoFixture fixture)
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed class Synthetic : IAsyncDisposable
    {
        public required IMongoDatabase Source { get; init; }
        public required IMongoDatabase Restored { get; init; }
        public required string SourceEvidence { get; init; }
        public required string RestoredEvidence { get; init; }
        public async ValueTask DisposeAsync()
        {
            await Source.Client.DropDatabaseAsync(Source.DatabaseNamespace.DatabaseName);
            await Restored.Client.DropDatabaseAsync(Restored.DatabaseNamespace.DatabaseName);
            foreach (var root in new[] { SourceEvidence, RestoredEvidence }) if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A synthetic source (validated collection with a unique index, a plain one, a migration ledger and nested
    /// evidence files) and a faithful copy of it, made document by document.</summary>
    private async Task<Synthetic> CreateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var data = new Synthetic {
            Source = fixture.Client.GetDatabase("unittoko_test_restore_src_" + suffix), Restored = fixture.Client.GetDatabase("unittoko_test_restore_dst_" + suffix),
            SourceEvidence = Path.Combine(Path.GetTempPath(), "unittoko-restore-src-" + suffix), RestoredEvidence = Path.Combine(Path.GetTempPath(), "unittoko-restore-dst-" + suffix),
        };
        foreach (var database in new[] { data.Source, data.Restored })
        {
            await database.CreateCollectionAsync("orders", new CreateCollectionOptions<BsonDocument> {
                Validator = new BsonDocumentFilterDefinition<BsonDocument>(new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "number" } } })) });
            await database.GetCollection<BsonDocument>("orders").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("number", 1), new CreateIndexOptions { Name = "number_unique", Unique = true }));
            await database.CreateCollectionAsync("notes");
            await database.CreateCollectionAsync("schemaMigrations");
        }
        var orders = Enumerable.Range(1, 40).Select(index => new BsonDocument { { "_id", (long)index }, { "number", "SO-" + index.ToString("D4") }, { "total", new BsonDecimal128(index * 1250.5m) } }).ToArray();
        var notes = Enumerable.Range(1, 5).Select(index => new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "text", "catatan " + index } }).ToArray();
        var migrations = new[] { new BsonDocument { { "_id", 117 }, { "state", "applied" } }, new BsonDocument { { "_id", 118 }, { "state", "applied" } } };
        foreach (var database in new[] { data.Source, data.Restored })
        {
            await database.GetCollection<BsonDocument>("orders").InsertManyAsync(orders.Select(document => document.DeepClone().AsBsonDocument));
            await database.GetCollection<BsonDocument>("notes").InsertManyAsync(notes.Select(document => document.DeepClone().AsBsonDocument));
            await database.GetCollection<BsonDocument>("schemaMigrations").InsertManyAsync(migrations.Select(document => document.DeepClone().AsBsonDocument));
        }
        foreach (var root in new[] { data.SourceEvidence, data.RestoredEvidence })
        {
            Directory.CreateDirectory(Path.Combine(root, "payments", "2026", "10"));
            await File.WriteAllBytesAsync(Path.Combine(root, "payments", "2026", "10", "proof-1.jpg"), Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
            await File.WriteAllBytesAsync(Path.Combine(root, "payments", "2026", "10", "proof-2.pdf"), Enumerable.Range(0, 2048).Select(index => (byte)(index % 13)).ToArray());
            await File.WriteAllTextAsync(Path.Combine(root, "store.json"), "{\"storeId\":\"synthetic\"}");
        }
        return data;
    }

    [Fact]
    public async Task An_exact_restore_verifies_clean_and_the_manifest_holds_no_document_content()
    {
        await using var data = await CreateAsync();
        var expected = await RestoreVerification.CaptureAsync(data.Source, data.SourceEvidence, Captured, default);
        var actual = await RestoreVerification.CaptureAsync(data.Restored, data.RestoredEvidence, Captured.AddHours(1), default);

        Assert.Empty(RestoreVerification.Compare(expected, actual));
        Assert.Equal(118, expected.SchemaVersion);
        Assert.Equal(["notes", "orders", "schemaMigrations"], expected.Collections.Select(collection => collection.Name));
        Assert.Equal(40, expected.Collections.Single(collection => collection.Name == "orders").Count);
        Assert.Equal(["payments/2026/10/proof-1.jpg", "payments/2026/10/proof-2.pdf", "store.json"], expected.Evidence.Select(item => item.Path));
        var json = System.Text.Json.JsonSerializer.Serialize(expected);
        foreach (var content in new[] { "SO-0001", "catatan", "storeId" }) Assert.DoesNotContain(content, json);
    }

    [Fact]
    public async Task Restore_drill_detects_a_missing_object()
    {
        await using var data = await CreateAsync();
        var expected = await RestoreVerification.CaptureAsync(data.Source, data.SourceEvidence, Captured, default);
        File.Delete(Path.Combine(data.RestoredEvidence, "payments", "2026", "10", "proof-2.pdf"));
        await File.WriteAllTextAsync(Path.Combine(data.RestoredEvidence, "store.json"), "{\"storeId\":\"changed\"}");
        await File.WriteAllTextAsync(Path.Combine(data.RestoredEvidence, "stray.tmp"), "x");

        var findings = RestoreVerification.Compare(expected, await RestoreVerification.CaptureAsync(data.Restored, data.RestoredEvidence, Captured, default));

        Assert.Equal(["evidence object missing: payments/2026/10/proof-2.pdf", "evidence object differs: store.json", "unexpected evidence object: stray.tmp"], findings);
    }

    [Fact]
    public async Task Restore_drill_detects_missing_documents_changed_content_lost_indexes_and_missing_collections()
    {
        await using var data = await CreateAsync();
        var expected = await RestoreVerification.CaptureAsync(data.Source, data.SourceEvidence, Captured, default);
        await data.Restored.GetCollection<BsonDocument>("orders").DeleteOneAsync(new BsonDocument("_id", 40L));
        await data.Restored.GetCollection<BsonDocument>("orders").Indexes.DropOneAsync("number_unique");
        await data.Restored.GetCollection<BsonDocument>("notes").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("text", "diubah")));
        await data.Restored.DropCollectionAsync("schemaMigrations");

        var findings = RestoreVerification.Compare(expected, await RestoreVerification.CaptureAsync(data.Restored, data.RestoredEvidence, Captured, default));

        Assert.Equal([
            "schema version differs: expected 118, found 0",
            "document content differs: notes",
            "document count differs: orders (expected 40, found 39)",
            "validator or indexes differ: orders",
            "collection missing: schemaMigrations",
        ], findings);
    }
}

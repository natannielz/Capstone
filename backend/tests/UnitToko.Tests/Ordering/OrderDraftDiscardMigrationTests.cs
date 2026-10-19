using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderDraftDiscardMigrationTests(MongoFixture fixture)
{
    internal static BsonDocument Row(long draftId, string key, DateTime discardedAt) => new() {
        { "_id", draftId }, { "schemaVersion", 1 }, { "kind", "ordering.draft.discard" }, { "discardedBy", 41L }, { "actorGrantVersion", 1L },
        { "context", new BsonDocument { { "organizationId", 10L }, { "branchId", 11L }, { "divisionId", BsonNull.Value } } },
        { "revision", 2L }, { "discardedAt", discardedAt }, { "restorableUntil", discardedAt.AddDays(30) },
        { "requestKey", key }, { "bodyHash", new string('a', 64) }, { "reason", "Tidak jadi dipesan" }, { "correlationId", "discard-fixture" }, { "auditId", 900L + draftId } };

    [Fact]
    public async Task Migration_118_creates_strict_empty_draft_discard_storage_with_a_thirty_day_window() {
        var database = fixture.Client.GetDatabase("order_draft_discard_migration_" + Guid.NewGuid().ToString("N"));
        try {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.True(CollectionMigrations.CurrentVersion >= 118);
            await OrderDraftDiscardSchemas.RequireStorageAsync(database, default);
            var rows = database.GetCollection<BsonDocument>(OrderDraftDiscardSchemas.Collection);
            Assert.Equal(0, await rows.CountDocumentsAsync(new BsonDocument()));
            var at = DateTime.SpecifyKind(new DateTime(2026, 10, 1, 3, 0, 0), DateTimeKind.Utc);
            await rows.InsertOneAsync(Row(7, "discard-7", at));
            // A window other than exactly 30 days, an unknown field, a missing date or a reused request key is refused.
            var longer = Row(8, "discard-8", at); longer["restorableUntil"] = at.AddDays(31);
            var extra = Row(9, "discard-9", at); extra["note"] = "x";
            var missing = Row(10, "discard-10", at); missing.Remove("discardedAt");
            var reused = Row(11, "discard-7", at);
            foreach (var row in new[] { longer, extra, missing, reused })
                await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(row));
            var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["key"].AsBsonDocument == new BsonDocument("restorableUntil", 1));
            await migrations.ApplyAsync();
            Assert.Equal(1, await rows.CountDocumentsAsync(new BsonDocument()));
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}

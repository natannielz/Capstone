using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankReceiptBson;
using C = UnitToko.Platform.Billing.BankTransactionClaims;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankTransactionClaimMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Backfill_proves_every_original_receipt_and_reimport_without_reposting_or_rewriting_history() {
        await using var data = await TwoCreditsAsync(fixture); var overlap = (await data.Statements.StageAsync("migration-overlap"))["importId"].AsInt64;
        await data.ConfirmAsync(await data.PreviewAsync(overlap), "migration-overlap-confirm");
        var expected = new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var before = await data.StateAsync(); var money = await BankTransactionClaimTests.MoneyAsync(data); var db = data.Statements.Accounts.Catalog.Database;
        var predecessor = await RestoreV55Async(data); await db.DropCollectionAsync(C.Collection);
        await new CollectionMigrations(db, data.Statements.Accounts.Catalog.Clock).ApplyAsync();
        Assert.Equal(expected, new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(money, await BankTransactionClaimTests.MoneyAsync(data));
        Assert.Equal(predecessor, new BsonArray((await HistoryAsync(data)).Take(55)));
        foreach (var receipt in await data.Rows(B.Receipts).Find(new BsonDocument()).ToListAsync())
            Assert.Equal(receipt["_id"], (await data.Service.ReadAsync(data.Reader, receipt["_id"].AsInt64))["_id"]);
    }
    [Fact]
    public async Task Exact_partial_backfill_resumes_with_same_collection_and_retained_stronger_guard() {
        await using var data = await TwoCreditsAsync(fixture); var db = data.Statements.Accounts.Catalog.Database;
        var expected = new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var original = await data.StateAsync(); await RestoreV55Async(data);
        await data.Rows(C.Collection).DeleteOneAsync(new BsonDocument("_id", expected[1]["_id"]));
        await data.Rows(C.Collection).Indexes.DropOneAsync("ux_" + C.Collection + "_1");
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", C.Collection },
            { "validator", new BsonDocument("$and", new BsonArray { BankTransactionClaimSchemas.Validator(), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var before = await DefinitionAsync(data); await new CollectionMigrations(db, data.Statements.Accounts.Catalog.Clock).ApplyAsync();
        var after = await DefinitionAsync(data); Assert.Equal(before["info"]["uuid"], after["info"]["uuid"]);
        Assert.True(InventorySchemas.Contains(after["options"]["validator"].AsBsonDocument, guard));
        Assert.Equal(expected, new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(original, await data.StateAsync()); var applied = await HistoryAsync(data);
        await new CollectionMigrations(db, data.Statements.Accounts.Catalog.Clock).ApplyAsync(); Assert.Equal(applied, await HistoryAsync(data)); Assert.Equal(after, await DefinitionAsync(data));
    }
    [Theory]
    [InlineData("claim")] [InlineData("journal")] [InlineData("effect")] [InlineData("guard")]
    public async Task Corrupt_claim_or_orphan_history_or_inadmissible_derived_claim_refuses_before_ddl(string fault) {
        await using var data = await TwoCreditsAsync(fixture); var db = data.Statements.Accounts.Catalog.Database; await RestoreV55Async(data);
        if (fault == "claim") {
            var claim = await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
            claim["ownerHash"] = new string('d', 64); claim["snapshotHash"] = B.Hash(claim);
            await data.Rows(C.Collection).ReplaceOneAsync(new BsonDocument("_id", claim["_id"]), claim);
        } else if (fault == "guard") {
            await data.Rows(C.Collection).DeleteManyAsync(new BsonDocument());
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", C.Collection },
                { "validator", new BsonDocument("$and", new BsonArray { BankTransactionClaimSchemas.Validator(), new BsonDocument("direction", "debit") }) },
                { "validationLevel", "strict" }, { "validationAction", "error" } });
        } else {
            // Delete a later original owner, leaving the real first physical bank anchor.
            var receipt = await data.Rows(B.Receipts).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
            var confirmation = await data.Rows(B.Confirmations).Find(new BsonDocument("_id", receipt["confirmationId"])).SingleAsync();
            await data.Rows(C.Collection).DeleteManyAsync(new BsonDocument());
            await data.Rows(B.Receipts).DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
            await data.Rows(B.Confirmations).DeleteOneAsync(new BsonDocument("_id", confirmation["_id"]));
            if (fault == "journal") {
                await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", confirmation["auditId"]));
                await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "actorId", confirmation["actorId"] }, { "operation", B.Operation }, { "key", confirmation["requestKey"] } });
                await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", B.SourceKey(confirmation)));
            } else await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", receipt["journalId"]));
        }
        var definition = await DefinitionAsync(data); var before = await data.StateAsync(); var history = new BsonArray((await HistoryAsync(data)).Take(55));
        Assert.NotNull(await Record.ExceptionAsync(() => new CollectionMigrations(db, data.Statements.Accounts.Catalog.Clock).ApplyAsync()));
        Assert.Equal(definition, await DefinitionAsync(data)); Assert.Equal(before, await data.StateAsync());
        Assert.Equal(history, new BsonArray((await HistoryAsync(data)).Take(55)));
    }
    private static async Task<BankReceiptTestData> TwoCreditsAsync(MongoFixture fixture) {
        var data = await BankReceiptTestData.CreateAsync(fixture);
        try {
            await data.ConfirmAsync(await data.PreviewAsync(), "migration-original");
            var next = (await data.Statements.StageAsync("migration-second", BankStatementTestData.Original("0002")))["importId"].AsInt64;
            await data.ConfirmAsync(await data.PreviewAsync(next), "migration-second-confirm", BankReceiptTestData.Observation("0002")); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    // Synthetic fixture predecessor only: prove the exact installed credit owners and
    // absence of outgoing settlement before removing the forward history marker.
    private static async Task<BsonArray> RestoreV55Async(BankReceiptTestData data) {
        // Only the known empty v56-v58 suffix is admitted. No populated postal
        // graph, settlement policy or command effect is erased to create v55.
        Assert.Equal(58, CollectionMigrations.CurrentVersion);
        foreach (var table in OrganizationAddressSchemas.Collections.Concat(RefundSettlementSchemas.Collections))
            Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^organization\.address(?:[.:]|$)"))).AnyAsync());
        Assert.False(await data.Rows(C.Settlements).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(C.Collection).Find(new BsonDocument("direction", "debit")).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("journalEntries", "source.namespace") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^billing\.refund\.payout\.verify(?:[.:]|$)"))).AnyAsync());
        var catalog = data.Statements.Accounts.Catalog;
        await BankTransactionClaimSchemas.RequireStorageAsync(catalog.Database, default);
        var claims = new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await new MongoUnitOfWork(catalog.Client).ExecuteAsync(async (tx, ct) => {
            var receipts = await data.Rows(B.Receipts).Find(tx, new BsonDocument()).ToListAsync(ct);
            Assert.Equal(receipts.LongCount(), await data.Rows(C.Collection).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct));
            foreach (var candidate in receipts) {
                var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(catalog.Database, tx, candidate["_id"].AsInt64, ct);
                await new C(catalog.Database).RequireCreditAsync(tx, receipt, ct);
            }
            return true;
        });
        var history = await HistoryAsync(data); Assert.Equal(CollectionMigrations.CurrentVersion, history.Count);
        Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), history.Select(row => row["_id"].AsInt32));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        var suffix = history.Skip(55).ToArray(); Assert.Equal(new[] { 56, 57, 58 }, suffix.Select(row => row["_id"].AsInt32));
        Assert.Equal(suffix.LongLength, (await data.Rows("schemaMigrations").DeleteManyAsync(new BsonDocument {
            { "_id", new BsonDocument("$in", new BsonArray(suffix.Select(record => record["_id"]))) }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(55)), await HistoryAsync(data));
        Assert.Equal(claims, new BsonArray(await data.Rows(C.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return await HistoryAsync(data);
    }
    private static async Task<BsonDocument> DefinitionAsync(BankReceiptTestData data) => await (await data.Statements.Accounts.Catalog.Database.ListCollectionsAsync(
        new ListCollectionsOptions { Filter = new BsonDocument("name", C.Collection) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(BankReceiptTestData data) => new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
}

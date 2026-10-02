using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using Data = UnitToko.Tests.Ordering.RefundSettlementTests.Data;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using P = UnitToko.Platform.Billing.RefundPayoutBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundSettlementMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_recorded_payout_and_historical_bank_head_upgrade_with_preserved_money_private_pin_and_stronger_empty_resume_guard() {
        await using var data = await Data.CreateAsync(fixture);
        // Use the real reviewed account lifecycle. Its current head changes while the
        // recorded payout retains its immutable, independently provable original head.
        var accounts = data.Bank.Statements.Accounts;
        var proposal = await accounts.ProposeAsync("settlement-upgrade-bank-v2", data.Payout.Input.PayingAccountId, 1);
        await accounts.ApproveAsync(proposal, await accounts.ObserveAsync(proposal, key: "settlement-upgrade-bank-v2-observe"), "settlement-upgrade-bank-v2-approve");
        var currentHead = await data.Rows(ReceivingAccountBson.Heads).Find(new BsonDocument("_id", data.Payout.Input.PayingAccountId)).SingleAsync();
        Assert.NotEqual(data.PayoutRecord["source"]["payingBank"]["headHash"], new BsonString(BankReceiptBson.Hash(currentHead)));
        await RestoreV56Async(data);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Events },
            { "validator", new BsonDocument("$and", new BsonArray { RefundSettlementSchemas.Validator(D.Events), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { Name = "operator_empty_resume_index" }));
        var firstKey = RefundSettlementSchemas.Keys(D.Events)[0];
        var missing = Assert.Single(await (await data.Rows(D.Events).Indexes.ListAsync()).ToListAsync(), index => index["key"] == firstKey);
        await data.Rows(D.Events).Indexes.DropOneAsync(missing["name"].AsString);
        var definitions = await DefinitionsAsync(data); var original = await data.PermanentAsync();
        var priorHistory = await HistoryAsync(data); var creditClaims = await ClaimsAsync(data);
        await UpgradeAsync(data);
        Assert.Equal(original, await data.PermanentAsync()); Assert.Equal(creditClaims, await ClaimsAsync(data));
        Assert.Equal(priorHistory, new BsonArray((await HistoryAsync(data)).Take(56)));
        var updated = await DefinitionsAsync(data);
        Assert.Equal(definitions.Where(row => !RefundSettlementSchemas.Collections.Contains(row["name"].AsString)),
            updated.Where(row => !RefundSettlementSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in RefundSettlementSchemas.Collections) {
            var before = Assert.Single(definitions, row => row["name"] == name); var after = Assert.Single(updated, row => row["name"] == name);
            Assert.Equal(before["info"]["uuid"], after["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(after["options"]["validator"].AsBsonDocument, RefundSettlementSchemas.Validator(name)));
            Assert.Equal("strict", after["options"]["validationLevel"]); Assert.Equal("error", after["options"]["validationAction"]);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in RefundSettlementSchemas.Keys(name)) Assert.True(Assert.Single(indexes, index => index["key"] == key)["unique"].AsBoolean);
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(updated, row => row["name"] == D.Events)["options"]["validator"].AsBsonDocument, guard));
        Assert.Contains(await (await data.Rows(D.Events).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "operator_empty_resume_index");
        Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", D.Events)).AnyAsync());
        var applied = await HistoryAsync(data); await UpgradeAsync(data);
        Assert.Equal(applied, await HistoryAsync(data)); Assert.Equal(updated, await DefinitionsAsync(data)); Assert.Equal(original, await data.PermanentAsync());
    }

    [WindowsStorageFact]
    public async Task Missing_actual_bank_version_cash_journal_private_pin_audit_or_record_receipt_refuses_before_any_settlement_DDL() {
        await using var data = await Data.CreateAsync(fixture); await RestoreV56Async(data);
        foreach (var (table, filter) in new[] {
            (ReceivingAccountBson.Versions, new BsonDocument("_id", data.Payout.Input.PayingAccountVersionId)),
            ("journalEntries", new BsonDocument("_id", data.PayoutRecord["source"]["payingBank"]["cashJournalId"])),
            ("evidencePinAudits", new BsonDocument("pinId", data.PayoutRecord["pinId"])),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Payout.Actor.UserId }, { "operation", P.Operation }, { "key", data.PayoutRecord["requestKey"] } }) }) {
            var actual = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
            try {
                var definitions = await DefinitionsAsync(data); var damaged = await data.PermanentAsync(); var prefix = new BsonArray((await HistoryAsync(data)).Take(56));
                Assert.NotNull(await Record.ExceptionAsync(() => UpgradeAsync(data)));
                Assert.Equal(definitions, await DefinitionsAsync(data)); Assert.Equal(damaged, await data.PermanentAsync());
                Assert.Equal(prefix, new BsonArray((await HistoryAsync(data)).Take(56)));
                Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 57 }, { "state", "applied" } }).AnyAsync());
                Assert.False(await data.Rows(table).Find(filter).AnyAsync());
                foreach (var name in RefundSettlementSchemas.Collections) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            } finally { await data.Rows(table).InsertOneAsync(actual); }
        }
        await UpgradeAsync(data); Assert.True(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 57 }, { "state", "applied" } }).AnyAsync());
    }

    private static async Task RestoreV56Async(Data data) {
        Assert.Equal(57, CollectionMigrations.CurrentVersion);
        Assert.False(await data.Rows(D.Events).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "debit")).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("journalEntries", "source.namespace") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^billing\.refund\.payout\.verify(?:[.:]|$)"))).AnyAsync());
        // Only the explicitly synthetic policy seeded by this isolated fixture is removed.
        var policy = await data.Rows(D.Policies).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("independent_exact_bank_debit", policy["rule"]);
        foreach (var field in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
            Assert.Equal("synthetic-test-only-" + field, policy[field]);
        Assert.Equal(1, (await data.Rows(D.Policies).DeleteOneAsync(policy)).DeletedCount);
        var history = await HistoryAsync(data); Assert.Equal(57, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 57 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(56)), await HistoryAsync(data));
    }
    private static Task UpgradeAsync(Data data) => new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(Data data) => (await (await data.Database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(Data data) => new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonArray> ClaimsAsync(Data data) => new(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
}

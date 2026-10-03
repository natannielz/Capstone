using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using Claims = UnitToko.Platform.Billing.BankTransactionClaims;
using Data = UnitToko.Tests.Ordering.ReturnFinancialApprovalTests.Data;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundPayoutMigrationTests(MongoFixture fixture)
{
    internal static async Task RemoveEmptyUpgradeAsync(IMongoDatabase database, bool dropTargets = true) {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        if (!await Rows("schemaMigrations").Find(new BsonDocument { { "_id", 55 }, { "state", "applied" } }).AnyAsync()) return;
        await UnitToko.Tests.Procurement.ProcurementDisbursementMigrationTests.RestoreV65Async(database);
        Assert.True(await Rows("schemaMigrations").Find(new BsonDocument { { "_id", 54 }, { "state", "applied" } }).AnyAsync());
        foreach (var name in RefundPayoutSchemas.Collections) Assert.Equal(0, await Rows(name).CountDocumentsAsync(new BsonDocument()));
        foreach (var name in RefundSettlementSchemas.Collections) Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("journalEntries", "source.namespace") })
            Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^billing\.refund\.payout\.verify(?:[.:]|$)"))).AnyAsync());
        Assert.False(await Rows(Claims.Collection).Find(new BsonDocument("direction", "debit")).AnyAsync());
        // The forward bank-claim resume retains real incoming owners. Prove every
        // immutable receipt/claim graph instead of deleting claims or inventing actors.
        await BankTransactionClaimSchemas.RequireStorageAsync(database, default);
        using (var tx = await database.Client.StartSessionAsync()) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            var receipts = await Rows(BankReceiptBson.Receipts).Find(tx, new BsonDocument()).ToListAsync();
            Assert.Equal(receipts.LongCount(), await Rows(Claims.Collection).CountDocumentsAsync(tx, new BsonDocument()));
            foreach (var row in receipts) {
                var actual = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, row["_id"].AsInt64, default);
                await new Claims(database).RequireCreditAsync(tx, actual, default);
            }
            await tx.AbortTransactionAsync();
        }
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(65, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        var suffix = history.Skip(54).ToArray(); Assert.Equal(65 - 54, suffix.Length);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^billing\.refund\.payout(?:[.:]|$)"))).AnyAsync());
        Assert.False(await Rows("journalEntries").Find(new BsonDocument("source.namespace", new BsonRegularExpression(@"^billing\.refund(?:[.:-]|$)"))).AnyAsync());
        Assert.False(await Rows("evidencePins").Find(new BsonDocument("associationKind", "RefundPayout")).AnyAsync());
        foreach (var table in new[] { "evidenceLineages", "evidenceUploadReservations", "evidenceReadLeases" })
            Assert.False(await Rows(table).Find(new BsonDocument("target.kind", "RefundPayout")).AnyAsync());
        foreach (var (table, field) in new[] { ("auditEvents", "operation"), ("idempotencyRecords", "operation") })
            Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^evidence\.refund-payout-image(?:[.:]|$)"))).AnyAsync());
        // Isolated fixtures only: replace the one exact known v55 branch, preserving every operator guard.
        foreach (var name in RefundPayoutEvidenceSchemas.Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
            var current = definition["options"]["validator"].AsBsonDocument; var found = false;
            BsonDocument Previous(BsonDocument row) {
                if (CatalogSchemas.SemanticEquals(row, RefundPayoutEvidenceSchemas.Validator(name))) { found = true; return RefundPayoutEvidenceSchemas.PreviousValidator(name); }
                var copy = row.DeepClone().AsBsonDocument;
                if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                    copy["$and"] = new BsonArray(terms.AsBsonArray.Select(value => value.IsBsonDocument ? Previous(value.AsBsonDocument) : value.DeepClone()));
                return copy;
            }
            var before = Previous(current); Assert.True(found);
            Assert.False(await Rows(name).Find(new BsonDocument("$nor", new BsonArray { before })).AnyAsync());
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", before }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        if (dropTargets) foreach (var name in RefundPayoutSchemas.Collections) await database.DropCollectionAsync(name);
        Assert.Equal(suffix.LongLength, (await Rows("schemaMigrations").DeleteManyAsync(new BsonDocument {
            { "_id", new BsonDocument("$in", new BsonArray(suffix.Select(row => row["_id"]))) }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(history.Take(54), await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    }
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase database) =>
        (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static Task UpgradeAsync(Data data) => new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
    private static async Task RefusedBeforeDdlAsync(Data data) {
        var definitions = await Definitions(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => UpgradeAsync(data)));
        Assert.Equal(definitions, await Definitions(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 55 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task<BsonDocument> ApproveAsync(Data data) => await data.ApproveAsync(await data.PreviewAsync(), Data.Meta("payout-upgrade-original-approval"));
    [WindowsStorageFact]
    public async Task Actual_approved_refund_upgrades_into_empty_recording_targets_without_touching_money_originals_or_predecessor_definitions() {
        await using var data = await Data.CreateAsync(fixture); await ApproveAsync(data); await RemoveEmptyUpgradeAsync(data.Database);
        var definitions = await Definitions(data.Database); var money = await data.MoneyAsync(); var original = await data.OriginalAsync();
        var approvals = await data.Rows(A.Events).Find(new BsonDocument()).ToListAsync();
        await UpgradeAsync(data);
        Assert.Equal(money, await data.MoneyAsync()); await data.AssertOriginalAsync(original); Assert.Equal(approvals, await data.Rows(A.Events).Find(new BsonDocument()).ToListAsync());
        var changed = RefundPayoutSchemas.Collections.Concat(RefundPayoutEvidenceSchemas.Collections).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(definitions.Where(row => !changed.Contains(row["name"].AsString)), (await Definitions(data.Database)).Where(row => !changed.Contains(row["name"].AsString)));
        foreach (var name in RefundPayoutSchemas.Collections) {
            Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString); Assert.Equal("error", definition["options"]["validationAction"].AsString);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in RefundPayoutSchemas.Keys(name)) Assert.True(Assert.Single(indexes, index => index["key"] == key)["unique"].AsBoolean);
        }
        foreach (var name in RefundPayoutEvidenceSchemas.Collections) {
            var before = Assert.Single(definitions, row => row["name"] == name);
            var after = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
            Assert.Equal(before["info"]["uuid"], after["info"]["uuid"]);
            Assert.True(ProcurementDisbursementEvidenceSchemas.Supports(name, after["options"]["validator"].AsBsonDocument, RefundPayoutEvidenceSchemas.Validator(name)));
        }
    }
    [WindowsStorageTheory]
    [InlineData("approval")] [InlineData("approval-journal")] [InlineData("approval-effect")] [InlineData("receipt-head")] [InlineData("bank-anchor")] [InlineData("bank-version")]
    public async Task Missing_real_approval_money_or_bank_source_refuses_before_any_payout_or_document_DDL(string fault) {
        await using var data = await Data.CreateAsync(fixture); var saved = await ApproveAsync(data); await RemoveEmptyUpgradeAsync(data.Database);
        var approval = await data.Rows(A.Events).Find(new BsonDocument("_id", saved["financialApprovalId"])).SingleAsync();
        var (table, filter) = fault switch {
            "approval" => (A.Events, new BsonDocument("_id", approval["_id"])), "approval-journal" => ("journalEntries", new BsonDocument("_id", approval["journalId"])),
            "approval-effect" => ("outboxEvents", new BsonDocument("sourceKey", A.SourceKey(approval))),
            "receipt-head" => (PaymentAllocationBson.Receipts, new BsonDocument()), "bank-anchor" => (BankReceiptBson.Accounts, new BsonDocument()),
            _ => (ReceivingAccountBson.Versions, new BsonDocument()) };
        Assert.True((await data.Rows(table).DeleteManyAsync(filter)).DeletedCount > 0); await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageTheory]
    [InlineData("draft-effect")] [InlineData("record-effect")] [InlineData("journal")]
    public async Task Orphan_new_operation_effect_or_outgoing_journal_cannot_initialize_empty_payout_history(string fault) {
        await using var data = await Data.CreateAsync(fixture); var saved = await ApproveAsync(data); await RemoveEmptyUpgradeAsync(data.Database);
        if (fault == "journal") {
            var journal = await data.Rows("journalEntries").Find(new BsonDocument()).FirstAsync(); journal["_id"] = 998876L;
            journal["source"]["namespace"] = "billing.refund-payout"; journal["source"]["documentId"] = 998876L; journal["source"]["eventKind"] = "verified";
            journal["sourceEvent"] = "billing.refund-payout:998876:verified:1";
            await data.Rows("journalEntries").InsertOneAsync(journal);
        } else {
            var effect = await data.Rows("outboxEvents").Find(new BsonDocument("kind", A.Operation)).SingleAsync(); effect["_id"] = ObjectId.GenerateNewId();
            effect["kind"] = fault == "draft-effect" ? B.DraftOperation : B.Operation; effect["sourceKey"] = effect["kind"].AsString + ":orphan";
            await data.Rows("outboxEvents").InsertOneAsync(effect);
        }
        await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageTheory]
    [InlineData("compatible")] [InlineData("ttl")] [InlineData("partial")] [InlineData("foreign-unique")]
    public async Task Empty_resume_preserves_stronger_guards_UUID_and_ordinary_indexes_but_rejects_unsafe_index_authority(string condition) {
        await using var data = await Data.CreateAsync(fixture); await ApproveAsync(data); await RemoveEmptyUpgradeAsync(data.Database);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
        await data.Database.CreateCollectionAsync(B.Payouts, new CreateCollectionOptions<BsonDocument> { Validator = guard });
        await data.Rows(B.Payouts).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), condition switch {
            "ttl" => new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1), Name = "operator_payout_index" },
            "partial" => new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("actorId", 999L), Name = "operator_payout_index" },
            "foreign-unique" => new CreateIndexOptions { Unique = true, Name = "operator_payout_index" }, _ => new CreateIndexOptions { Name = "operator_payout_index" } }));
        if (condition != "compatible") { await RefusedBeforeDdlAsync(data); return; }
        var before = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", B.Payouts) })).SingleAsync();
        await UpgradeAsync(data);
        var after = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", B.Payouts) })).SingleAsync();
        Assert.Equal(before["info"]["uuid"], after["info"]["uuid"]); Assert.True(InventorySchemas.Contains(after["options"]["validator"].AsBsonDocument, guard));
        Assert.True(InventorySchemas.Contains(after["options"]["validator"].AsBsonDocument, RefundPayoutSchemas.Validator(B.Payouts)));
        Assert.Contains(await (await data.Rows(B.Payouts).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "operator_payout_index");
    }
}

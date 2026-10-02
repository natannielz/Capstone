using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;
using Data = UnitToko.Tests.Ordering.ReturnEntitlementTests.Data;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnFinancialApprovalMigrationTests(MongoFixture fixture)
{
    internal static async Task RemoveEmptyUpgradeAsync(IMongoDatabase database, bool dropTargets = true) {
        await RefundPayoutMigrationTests.RemoveEmptyUpgradeAsync(database);
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        Assert.True(await Rows("schemaMigrations").Find(new BsonDocument { { "_id", 53 }, { "state", "applied" } }).AnyAsync());
        Assert.True(await Rows("schemaMigrations").Find(new BsonDocument { { "_id", 54 }, { "state", "applied" } }).AnyAsync());
        foreach (var name in ReturnFinancialApprovalSchemas.Collections) Assert.Equal(0, await Rows(name).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(A.Operation) + @"(?:[.:]|$)"))).AnyAsync());
        Assert.False(await Rows("journalEntries").Find(new BsonDocument("source.namespace", "billing.return-financial")).AnyAsync());
        // Only isolated fixtures may remove the proven-empty suffix to establish a real predecessor.
        if (dropTargets) foreach (var name in ReturnFinancialApprovalSchemas.Collections) await database.DropCollectionAsync(name);
        Assert.Equal(1, (await Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 54 }, { "state", "applied" } })).DeletedCount);
    }
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase database) =>
        (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task PredecessorAsync(Data data) {
        await RemoveEmptyUpgradeAsync(data.Database);
    }
    private static Task UpgradeAsync(Data data) => new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
    private static async Task RefusedBeforeDdlAsync(Data data) {
        var definitions = await Definitions(data.Database);
        Assert.NotNull(await Record.ExceptionAsync(() => UpgradeAsync(data)));
        Assert.Equal(definitions, await Definitions(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 54 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task RequestAsync(Data data, string key = "financial-upgrade-request") { await data.RequestAsync(await data.PreviewAsync(), Data.Meta(key)); }
    [WindowsStorageFact]
    public async Task Actual_paid_invoice_and_exact_inspected_financial_request_upgrade_without_mutating_any_predecessor_definition_or_business_history() {
        await using var data = await Data.CreateAsync(fixture, "refund", paid: true); await RequestAsync(data); await PredecessorAsync(data);
        var definitions = await Definitions(data.Database); var business = await data.BusinessAsync();
        var history = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in ReturnEntitlementSchemas.Collections) history.Add(name, await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await UpgradeAsync(data);
        Assert.Equal(definitions, (await Definitions(data.Database)).Where(row => !ReturnFinancialApprovalSchemas.Collections.Contains(row["name"].AsString)).ToList());
        await data.AssertBusinessAsync(business);
        foreach (var item in history) Assert.Equal(item.Value, await data.Rows(item.Key).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var name in ReturnFinancialApprovalSchemas.Collections) {
            Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in ReturnFinancialApprovalSchemas.Keys(name)) Assert.True(Assert.Single(indexes, index => index["key"].Equals(key))["unique"].AsBoolean);
            Assert.DoesNotContain(indexes, index => index["key"].AsBsonDocument.Names.Any(field => field.StartsWith("receipts.", StringComparison.Ordinal)) && index.GetValue("unique", false).AsBoolean);
        }
        await ReturnFinancialApprovalStorage.RequireAsync(data.Database, default);
        Assert.True(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 54 }, { "state", "applied" } }).AnyAsync());
    }
    [WindowsStorageTheory]
    [InlineData("request")] [InlineData("pool")] [InlineData("proposal")] [InlineData("recipient-receipt")] [InlineData("invoice-journal")] [InlineData("request-effect")]
    public async Task Missing_original_history_or_request_effect_refuses_before_new_approval_DDL(string fault) {
        await using var data = await Data.CreateAsync(fixture); await RequestAsync(data); await PredecessorAsync(data);
        var (table, filter) = fault switch {
            "request" => (E.Requests, new BsonDocument("_id", data.ReturnId)), "pool" => (E.Pools, new BsonDocument("_id", data.Payment.Issued.Id.Value)),
            "proposal" => (ReturnResolutionRecords.Collection, new BsonDocument("_id", data.ReturnId)), "recipient-receipt" => (ShipmentReceiptRecords.Receipts, new BsonDocument()),
            "request-effect" => ("outboxEvents", new BsonDocument("sourceKey", E.Operation + ":" + data.ReturnId)),
            _ => ("journalEntries", new BsonDocument("_id", (await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync())["journalId"])) };
        Assert.True((await data.Rows(table).DeleteManyAsync(filter)).DeletedCount > 0); await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageFact]
    public async Task Coherently_rehashed_request_before_actual_proposal_still_fails_actual_source_chronology_preflight() {
        await using var data = await Data.CreateAsync(fixture); var metadata = Data.Meta("financial-upgrade-chronology");
        await data.RequestAsync(await data.PreviewAsync(), metadata); await PredecessorAsync(data);
        var proposal = await data.Rows(ReturnResolutionRecords.Collection).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        var row = await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        var earlier = new BsonDateTime(proposal["requestedAt"].ToUniversalTime().AddMilliseconds(-1));
        Assert.True(earlier.CompareTo(row["policy"]["approvedAt"]) >= 0);
        row["requestedAt"] = earlier; row["snapshotHash"] = B.Hash(row); var result = E.Result(row);
        await data.Rows(E.Requests).ReplaceOneAsync(new BsonDocument("_id", data.ReturnId), row);
        await data.Rows("auditEvents").ReplaceOneAsync(new BsonDocument("_id", row["auditId"]), E.Audit(row));
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("sourceKey", E.Operation + ":" + data.ReturnId), Builders<BsonDocument>.Update.Set("createdAt", earlier).Set("dueAt", earlier).Set("payload", result));
        await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument { { "actorId", data.Actor.UserId }, { "operation", E.Operation }, { "key", metadata.IdempotencyKey } }, Builders<BsonDocument>.Update.Set("createdAt", earlier).Set("result", result));
        var head = await data.Rows(E.Pools).Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync();
        head["lastRequestHash"] = row["snapshotHash"]; head["snapshotHash"] = B.Hash(head); await data.Rows(E.Pools).ReplaceOneAsync(new BsonDocument("_id", head["_id"]), head);
        Assert.Equal(row, await new MongoUnitOfWork(data.Payment.Invoice.Order.Prices.Catalog.Client).ExecuteAsync((tx, ct) => new ReturnEntitlementRecords(data.Database, data.Clock).RawAsync(tx, data.ReturnId, ct)));
        await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageFact]
    public async Task Coherently_rehashed_original_account_identity_cannot_replace_the_actual_issued_journal_identity() {
        await using var data = await Data.CreateAsync(fixture); var metadata = Data.Meta("financial-upgrade-original-account");
        await data.RequestAsync(await data.PreviewAsync(), metadata); await PredecessorAsync(data);
        var row = await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        row["source"]["originalAccounts"][0]["accountId"] = 998779L;
        row["binding"] = E.Binding(row["source"].AsBsonDocument, row["before"].AsBsonDocument, row["policy"].AsBsonDocument);
        row["bodyHash"] = AuthorizationStore.CommandHash(E.Body(data.CaseId, data.ReturnId, data.Action, row["before"]["version"].AsInt64, row["binding"].AsString, E.Input(row["input"].AsBsonDocument)), row["reason"].AsString);
        row["snapshotHash"] = B.Hash(row); var result = E.Result(row);
        await data.Rows(E.Requests).ReplaceOneAsync(new BsonDocument("_id", data.ReturnId), row);
        await data.Rows("auditEvents").ReplaceOneAsync(new BsonDocument("_id", row["auditId"]), E.Audit(row));
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("sourceKey", E.Operation + ":" + data.ReturnId), Builders<BsonDocument>.Update.Set("payload", result));
        await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument { { "actorId", data.Actor.UserId }, { "operation", E.Operation }, { "key", metadata.IdempotencyKey } }, Builders<BsonDocument>.Update.Set("bodyHash", row["bodyHash"]).Set("result", result));
        var head = await data.Rows(E.Pools).Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync();
        head["lastRequestHash"] = row["snapshotHash"]; head["snapshotHash"] = B.Hash(head); await data.Rows(E.Pools).ReplaceOneAsync(new BsonDocument("_id", head["_id"]), head);
        Assert.Equal(row, await new MongoUnitOfWork(data.Payment.Invoice.Order.Prices.Catalog.Client).ExecuteAsync((tx, ct) => new ReturnEntitlementRecords(data.Database, data.Clock).RawAsync(tx, data.ReturnId, ct)));
        await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageTheory]
    [InlineData("approval-effect")] [InlineData("approval-journal")] [InlineData("entitlement-orphan")]
    public async Task Orphan_new_approval_effects_or_old_financial_effects_never_initialize_empty_approval_history(string fault) {
        await using var data = await Data.CreateAsync(fixture); await RequestAsync(data); await PredecessorAsync(data);
        if (fault == "approval-journal") {
            var journal = await data.Rows("journalEntries").Find(new BsonDocument()).FirstAsync(); journal["_id"] = 998777L;
            var orphan = ReturnFinancialApprovalPostingSource.Source(998778L); journal["source"] = AccountingBson.Source(orphan); journal["sourceEvent"] = orphan.EventKey;
            await data.Rows("journalEntries").InsertOneAsync(journal);
        } else {
            var effect = await data.Rows("outboxEvents").Find(new BsonDocument("kind", E.Operation)).SingleAsync();
            effect["_id"] = ObjectId.GenerateNewId(); effect["kind"] = (fault == "entitlement-orphan" ? E.Operation : A.Operation) + ".orphan";
            effect["sourceKey"] = effect["kind"].AsString + ":orphan"; await data.Rows("outboxEvents").InsertOneAsync(effect);
        }
        await RefusedBeforeDdlAsync(data);
    }
    [WindowsStorageTheory]
    [InlineData("compatible")] [InlineData("ttl")] [InlineData("partial")] [InlineData("foreign-unique")]
    public async Task Empty_resume_keeps_stronger_operator_guard_and_ordinary_index_but_refuses_unsafe_or_unapproved_uniqueness(string condition) {
        await using var data = await Data.CreateAsync(fixture); await RequestAsync(data); await PredecessorAsync(data);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
        await data.Database.CreateCollectionAsync(A.Events, new CreateCollectionOptions<BsonDocument> { Validator = guard });
        await data.Rows(A.Events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), condition switch {
            "ttl" => new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(1), Name = "operator_financial_index" },
            "partial" => new CreateIndexOptions<BsonDocument> { PartialFilterExpression = new BsonDocument("action", "credit"), Name = "operator_financial_index" },
            "foreign-unique" => new CreateIndexOptions { Unique = true, Name = "operator_financial_index" }, _ => new CreateIndexOptions { Name = "operator_financial_index" } }));
        if (condition != "compatible") { await RefusedBeforeDdlAsync(data); return; }
        await UpgradeAsync(data); await ReturnFinancialApprovalStorage.RequireAsync(data.Database, default);
        var definitions = await Definitions(data.Database); var target = Assert.Single(definitions, row => row["name"] == A.Events);
        Assert.True(InventorySchemas.Contains(target["options"]["validator"].AsBsonDocument, guard));
        Assert.Equal("strict", target["options"]["validationLevel"]); Assert.Equal("error", target["options"]["validationAction"]);
        Assert.Single(await (await data.Rows(A.Events).Indexes.ListAsync()).ToListAsync(), index => index["name"] == "operator_financial_index");
        await RemoveEmptyUpgradeAsync(data.Database, dropTargets: false); await UpgradeAsync(data); Assert.Equal(definitions, await Definitions(data.Database));
    }
    [WindowsStorageFact]
    public async Task Runtime_storage_refuses_partial_index_even_on_the_single_scope_policy_key() {
        await using var data = await Data.CreateAsync(fixture); await ReturnFinancialApprovalStorage.RequireAsync(data.Database, default);
        await data.Rows(A.Policies).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("active", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unsafe_partial_policy_index", PartialFilterExpression = new BsonDocument("active", true) }));
        Assert.NotNull(await Record.ExceptionAsync(() => ReturnFinancialApprovalStorage.RequireAsync(data.Database, default)));
    }
}

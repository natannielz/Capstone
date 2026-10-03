using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementSettlementMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_credit_and_verified_refund_claims_survive_exact_v2_upgrade_and_missing_owner_claim_or_journal_audit_refuses_before_DDL()
    {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var saved = await data.VerifyAsync(await data.PreviewAsync(), "refund-before67");
        var settlement = await data.Rows(RefundSettlementBson.Events).Find(new BsonDocument()).SingleAsync();
        var debit = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "debit")).SingleAsync();
        Assert.Equal(BankTransactionClaims.Debit(settlement), debit);
        Assert.True(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "credit")).AnyAsync());
        await RestoreV66Async(data.Database);
        Assert.Equal(BankTransactionClaimSchemas.Validator(), ProcurementBankClaimSchemas.Validator()["$or"][0]);
        var counter = await data.Rows("idCounters").Find(new BsonDocument("_id", BankReceiptBson.Receipts)).SingleAsync();
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", BankReceiptBson.Receipts),
            Builders<BsonDocument>.Update.Set("value", checked(counter["value"].AsInt64 + 1000L)));
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection },
            { "validator", new BsonDocument("$and", new BsonArray { BankTransactionClaimSchemas.Validator(), guard }) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        var definition = await DefinitionAsync(data.Database, BankTransactionClaims.Collection);
        var retained = await PreservedAsync(data.Database); var history = await HistoryAsync(data.Database);
        var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        foreach (var (name, filter) in new[] {
            (RefundSettlementBson.Events, new BsonDocument("_id", settlement["_id"])),
            (BankTransactionClaims.Collection, new BsonDocument("_id", debit["_id"])),
            ("accountingAuditEvents", new BsonDocument("journalId", settlement["journalId"])) }) {
            var original = await data.Rows(name).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(name).DeleteOneAsync(filter)).DeletedCount);
            try {
                var broken = await PreservedAsync(data.Database);
                Assert.Equal("Procurement settlement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
                Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
                Assert.Equal(broken, await PreservedAsync(data.Database));
                Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(66)));
                Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 67 }, { "state", "applied" } }).AnyAsync());
            } finally { await data.Rows(name).InsertOneAsync(original); }
        }
        Assert.Equal(retained, await PreservedAsync(data.Database));
        var expectedRetained = await PreservedAsync(data.Database, withUpgradeHighWater: true);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(expectedRetained, await PreservedAsync(data.Database));
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(66)));
        var after = await DefinitionAsync(data.Database, BankTransactionClaims.Collection);
        Assert.Equal(definition["info"]["uuid"], after["info"]["uuid"]);
        Assert.Equal(ProcurementBankClaimSchemas.Extend(definition["options"]["validator"].AsBsonDocument), after["options"]["validator"]);
        Assert.True(InventorySchemas.Contains(after["options"]["validator"].AsBsonDocument, guard));
        await RequireInstalledIndexesAsync(data.Database);
        Assert.Equal(67, (await HistoryAsync(data.Database)).Count);
        var applied = await HistoryAsync(data.Database); var completedDefinitions = await DefinitionsAsync(data.Database); var completedIndexes = await IndexesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(completedDefinitions, await DefinitionsAsync(data.Database));
        Assert.Equal(completedIndexes, await IndexesAsync(data.Database)); Assert.Equal(expectedRetained, await PreservedAsync(data.Database));
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, "refund-before67"));
        await using var originalRead = await data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, data.PayoutId, data.Payout.Reservation.ReservationId, data.Payout.Reference);
        using var bytes = new MemoryStream(); await originalRead.Content.CopyToAsync(bytes); Assert.Equal(data.Payout.Bytes, bytes.ToArray());
    }

    [Fact]
    public async Task Empty_partial_index_transition_resumes_preserving_guards_and_rejects_unknown_partial_or_orphan_new_owner_before_DDL()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV66Async(data.Database);
        var rows = data.Database.GetCollection<BsonDocument>(BankTransactionClaims.Collection);
        var before = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("ownerId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_partial", Unique = true, PartialFilterExpression = new BsonDocument("direction", "debit") }));
        try {
            var damagedIndexes = await IndexesAsync(data.Database);
            Assert.Equal("Procurement settlement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
            Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(damagedIndexes, await IndexesAsync(data.Database));
        } finally { await rows.Indexes.DropOneAsync("unknown_partial"); }
        Assert.Equal(indexes, await IndexesAsync(data.Database));
        var orphan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", ProcurementSettlementBson.Operation } };
        await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(orphan, new InsertOneOptions { BypassDocumentValidation = true });
        try {
            var broken = await PreservedAsync(data.Database);
            Assert.Equal("Procurement settlement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
            Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database)); Assert.Equal(broken, await PreservedAsync(data.Database));
        } finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankReceiptBson.Receipts }, { "validationLevel", "off" } });
        try {
            var brokenDefinitions = await DefinitionsAsync(data.Database);
            Assert.Equal("Procurement settlement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
            Assert.Equal(brokenDefinitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankReceiptBson.Receipts }, { "validationLevel", "strict" } }); }
        // Exact interrupted DDL is resumable. Unknown partial filters above were refused.
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ProcurementBankClaimSchemas.V1Key, new CreateIndexOptions<BsonDocument> {
            Name = ProcurementBankClaimSchemas.V1Index, Unique = true, PartialFilterExpression = ProcurementBankClaimSchemas.V1Filter }));
        var state = await PreservedAsync(data.Database, withUpgradeHighWater: true); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(state, await PreservedAsync(data.Database)); await RequireInstalledIndexesAsync(data.Database);
        foreach (var name in ProcurementSettlementSchemas.Collections) {
            Assert.False(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_recorded_supplier_original_and_capacity_survive_upgrade_and_missing_pin_audit_refuses_before_DDL()
    {
        await using var data = await ProcurementDisbursementTestData.CreateAsync(fixture);
        await data.UploadAsync(); await data.ValidateAsync();
        var saved = await data.RecordAsync(await data.PreviewRecordAsync(), ProcurementDisbursementTestData.Meta("actual-record-before67"));
        await RestoreV66Async(data.Database);
        var pinId = (await data.Rows(ProcurementDisbursementBson.Records).Find(new BsonDocument("_id", saved["disbursementId"])).SingleAsync())["pinId"];
        var audit = await data.Rows("evidencePinAudits").Find(new BsonDocument("pinId", pinId)).SingleAsync();
        var before = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        Assert.Equal(1, (await data.Rows("evidencePinAudits").DeleteOneAsync(new BsonDocument("_id", audit["_id"]))).DeletedCount);
        try {
            var broken = await PreservedAsync(data.Database);
            Assert.Equal("Procurement settlement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync())).Message);
            Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database)); Assert.Equal(broken, await PreservedAsync(data.Database));
        } finally { await data.Rows("evidencePinAudits").InsertOneAsync(audit); }
        var retained = await PreservedAsync(data.Database, withUpgradeHighWater: true); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await PreservedAsync(data.Database)); await RequireInstalledIndexesAsync(data.Database);
    }

    internal static async Task RestoreV66Async(IMongoDatabase database)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var history = await HistoryAsync(database);
        if (history.Count <= 66) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(67, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var name in ProcurementSettlementSchemas.Collections) {
            Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.settlement(?:[.:]|$)"))).AnyAsync());
        Assert.False(await Rows(BankTransactionClaims.Collection).Find(new BsonDocument("$nor", new BsonArray { BankTransactionClaimSchemas.Validator() })).AnyAsync());
        await RequireInstalledIndexesAsync(database);
        var definition = await DefinitionAsync(database, BankTransactionClaims.Collection); var found = false;
        BsonDocument Previous(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, ProcurementBankClaimSchemas.Validator())) { found = true; return BankTransactionClaimSchemas.Validator(); }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Previous(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var previous = Previous(definition["options"]["validator"].AsBsonDocument); Assert.True(found);
        Assert.False(await Rows(BankTransactionClaims.Collection).Find(new BsonDocument("$nor", new BsonArray { previous })).AnyAsync());
        // Fixture-only reversal, after the entire newer owner and v2 branch have proved empty.
        await Rows(BankTransactionClaims.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ProcurementBankClaimSchemas.V1Key,
            new CreateIndexOptions { Name = "ux_bankTransactionClaims_1", Unique = true }));
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection }, { "validator", previous }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Rows(BankTransactionClaims.Collection).Indexes.DropOneAsync(ProcurementBankClaimSchemas.V1Index);
        await Rows(BankTransactionClaims.Collection).Indexes.DropOneAsync(ProcurementBankClaimSchemas.V2Index);
        await BankTransactionClaimSchemas.RequireStorageAsync(database, CancellationToken.None);
        Assert.Equal(1, (await Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 67 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(66)), await HistoryAsync(database));
    }
    internal static async Task RequireInstalledIndexesAsync(IMongoDatabase database) {
        await ProcurementBankClaimSchemas.RequireStorageAsync(database, CancellationToken.None);
        var indexes = await (await database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).Indexes.ListAsync()).ToListAsync();
        foreach (var (name, key, filter) in new[] { (ProcurementBankClaimSchemas.V1Index, ProcurementBankClaimSchemas.V1Key, ProcurementBankClaimSchemas.V1Filter),
            (ProcurementBankClaimSchemas.V2Index, ProcurementBankClaimSchemas.V2Key, ProcurementBankClaimSchemas.V2Filter) }) {
            var index = Assert.Single(indexes, index => index["name"] == name); Assert.True(index["unique"].AsBoolean);
            Assert.Equal(key, index["key"]); Assert.Equal(filter, index["partialFilterExpression"]);
        }
        Assert.DoesNotContain(indexes, index => index["key"] == ProcurementBankClaimSchemas.V1Key && !index.Contains("partialFilterExpression"));
        var canonical = Assert.Single(indexes, index => index["key"] == ProcurementBankClaimSchemas.CanonicalKey); Assert.True(canonical["unique"].AsBoolean);
        Assert.False(canonical.Contains("partialFilterExpression"));
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase database, string name) => await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase database) {
        var result = new BsonDocument();
        foreach (var name in ProcurementSettlementSchemas.Collections.Append(BankTransactionClaims.Collection))
            result[name] = new BsonArray((await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString));
        return result;
    }
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database, bool withUpgradeHighWater = false) {
        var result = new BsonDocument();
        foreach (var name in ReceivingAccountSchemas.Collections.Concat(BankStatementSchemas.Collections).Concat(BankReceiptSchemas.Collections).Concat(RefundPayoutSchemas.Collections)
            .Concat(RefundSettlementSchemas.Collections).Concat(ProcurementDisbursementSchemas.Collections).Concat(ProcurementSettlementSchemas.Collections)
            .Concat(new[] { "bankTransactionClaims", "idCounters", "idempotencyRecords", "auditEvents", "outboxEvents", "journalEntries", "accountingAuditEvents", "accountingBooks", "accountingChartVersions", "periods", "evidencePins", "evidencePinAudits", "evidenceReadLeases", "documentVersions", "evidenceValidationResults" }).Distinct(StringComparer.Ordinal))
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        if (withUpgradeHighWater) {
            // Only the documented monotonic retained-ID high-water repair may alter counters.
            // Operator-supplied policy IDs need a counter on upgrade; no owner facts are created.
            var counters = result["idCounters"].AsBsonArray.Select(row => row.DeepClone().AsBsonDocument).ToDictionary(row => row["_id"].AsString, StringComparer.Ordinal);
            foreach (var name in CatalogSchemas.Collections.Concat(ProcurementRequestSchemas.Collections).Concat(SupplierSchemas.Collections)
                .Concat(PurchaseOrderSchemas.Collections).Concat(ProcurementFundingSchemas.Collections).Concat(SupplierBankSchemas.Collections)
                .Concat(ProcurementDisbursementSchemas.Collections).Concat(ReceivingAccountSchemas.Collections).Concat(BankStatementSchemas.Collections)
                .Concat(BankReceiptSchemas.Collections).Concat(RefundPayoutSchemas.Collections).Append("auditEvents").Distinct(StringComparer.Ordinal)) {
                var maximum = await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync();
                if (maximum is null) continue;
                if (counters.TryGetValue(name, out var counter)) counter["value"] = Math.Max(counter["value"].AsInt64, maximum["_id"].AsInt64);
                else counters.Add(name, new BsonDocument { { "_id", name }, { "value", maximum["_id"] } });
            }
            result["idCounters"] = new BsonArray(counters.Values.OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        }
        return result;
    }
}

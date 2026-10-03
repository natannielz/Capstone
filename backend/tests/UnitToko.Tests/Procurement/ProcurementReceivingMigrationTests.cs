using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementReceivingMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_v2_supplier_settlement_and_private_pin_survive_upgrade_and_missing_claim_owner_or_journal_audit_refuses_before_DDL()
    {
        await using var data = await ProcurementSettlementTestData.CreateAsync(fixture);
        var saved = await data.VerifyAsync(await data.PreviewAsync(), "settlement-before68");
        var settlement = await data.Rows(ProcurementSettlementBson.Events).Find(new BsonDocument("_id", saved["id"])).SingleAsync();
        var claim = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("_id", settlement["canonicalIdentity"])).SingleAsync();
        Assert.Equal(2, claim["schemaVersion"]); Assert.Equal("debit", claim["direction"]);
        Assert.Equal(ProcurementSettlementBson.ClaimOwner, claim["ownerKind"]); Assert.Equal(ProcurementSettlementBson.Claim(settlement), claim);
        Assert.True(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument { { "schemaVersion", 1 }, { "direction", "credit" } }).AnyAsync());
        await RestoreV67Async(data.Database);
        var extra = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection },
            { "validator", new BsonDocument("$and", new BsonArray { ProcurementBankClaimSchemas.Validator(), extra }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        var history = await HistoryAsync(data.Database);
        foreach (var (name, filter) in new[] {
            (BankTransactionClaims.Collection, new BsonDocument("_id", claim["_id"])),
            (ProcurementSettlementBson.Events, new BsonDocument("_id", settlement["_id"])),
            ("accountingAuditEvents", new BsonDocument("journalId", settlement["journalId"])) }) {
            var row = await data.Rows(name).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(name).DeleteOneAsync(filter)).DeletedCount);
            try {
                var broken = await StateAsync(data.Database, includeCounters: true);
                await RefusesAsync(data.Database, data.Clock);
                Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
                Assert.Equal(broken, await StateAsync(data.Database, includeCounters: true));
                Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(67)));
            } finally { await data.Rows(name).InsertOneAsync(row); }
        }
        var retained = await StateAsync(data.Database); var counters = await CountersAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database)); Assert.Equal(definitions, await DefinitionsAsync(data.Database));
        Assert.Equal(indexes, await IndexesAsync(data.Database)); await RequireHighWaterAsync(data.Database, counters);
        await ProcurementSettlementMigrationTests.RequireInstalledIndexesAsync(data.Database); await RequireEmptyInstalledAsync(data.Database);
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(67)));
        Assert.True(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 68 }, { "state", "applied" } }).AnyAsync());
        Assert.Equal(saved, await data.Service.LookupAsync(data.Verifier, ProcurementSettlementBson.Operation, "settlement-before68"));
        Assert.Equal(claim, await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("_id", claim["_id"])).SingleAsync());
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_v1_credit_and_verified_refund_preserve_original_claims_journals_and_private_original_across_v68()
    {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var saved = await data.VerifyAsync(await data.PreviewAsync(), "refund-before68");
        var claims = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Contains(claims, row => row["schemaVersion"] == 1 && row["direction"] == "credit");
        Assert.Contains(claims, row => row["schemaVersion"] == 1 && row["direction"] == "debit");
        await RestoreV67Async(data.Database);
        var retained = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database)); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        Assert.Equal(BankTransactionClaimSchemas.Validator(), ProcurementBankClaimSchemas.Validator()["$or"][0]);
        await ProcurementSettlementMigrationTests.RequireInstalledIndexesAsync(data.Database); await RequireEmptyInstalledAsync(data.Database);
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, "refund-before68"));
        await using var original = await data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, data.PayoutId, data.Payout.Reservation.ReservationId, data.Payout.Reference);
        using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Payout.Bytes, bytes.ToArray());
    }

    [Fact]
    public async Task Empty_partial_install_preserves_stronger_guards_rejects_unknown_indexes_or_orphan_effects_and_never_seeds_receiving_facts()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV67Async(data.Database);
        var notices = data.Database.GetCollection<BsonDocument>(B.Notices);
        var extra = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Notices },
            { "validator", new BsonDocument("$and", new BsonArray { ProcurementReceivingSchemas.Validator(B.Notices), extra }) } });
        await notices.Indexes.DropOneAsync("ux_" + B.Notices + "_0");
        await notices.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("orderId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_receiving_partial", Unique = true, PartialFilterExpression = new BsonDocument("version", 1L) }));
        try {
            var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
            await RefusesAsync(data.Database, data.Clock); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        } finally { await notices.Indexes.DropOneAsync("unknown_receiving_partial"); }
        var orphan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", B.IntakeOperation } };
        var effects = data.Database.GetCollection<BsonDocument>("outboxEvents");
        await effects.InsertOneAsync(orphan, new InsertOneOptions { BypassDocumentValidation = true });
        try {
            var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database); var state = await StateAsync(data.Database, true);
            await RefusesAsync(data.Database, data.Clock); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database)); Assert.Equal(state, await StateAsync(data.Database, true));
        } finally { Assert.Equal(1, (await effects.DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", ProcurementSettlementBson.Events }, { "validationLevel", "off" } });
        try {
            var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
            await RefusesAsync(data.Database, data.Clock); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", ProcurementSettlementBson.Events }, { "validationLevel", "strict" } }); }
        // Heads use the notice ID; an allocator here is evidence of an incompatible owner.
        var counter = new BsonDocument { { "_id", B.Heads }, { "value", 0L } };
        await data.Database.GetCollection<BsonDocument>("idCounters").InsertOneAsync(counter);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", B.Heads))).DeletedCount); }
        var retained = await StateAsync(data.Database, true); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database, true)); await RequireEmptyInstalledAsync(data.Database);
        var definition = await DefinitionAsync(data.Database, B.Notices);
        Assert.True(InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, extra));
        var completed = await DefinitionsAsync(data.Database); var installedIndexes = await IndexesAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(completed, await DefinitionsAsync(data.Database)); Assert.Equal(installedIndexes, await IndexesAsync(data.Database)); Assert.Equal(history, await HistoryAsync(data.Database));
        var malformedHead = new BsonDocument { { "_id", 8001L }, { "schemaVersion", 1 }, { "noticeId", 8001L }, { "noticeHash", new string('a', 64) },
            { "version", 0L }, { "lastIntakeId", BsonNull.Value }, { "lastHash", BsonNull.Value }, { "matchedContainers", 0 }, { "excessContainers", 0L },
            { "heldContainers", 1L }, { "receivedContainers", 0L }, { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "fence", 0L } };
        var rejected = await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>(B.Heads).InsertOneAsync(malformedHead));
        Assert.Equal(121, rejected.WriteError.Code); await RequireEmptyInstalledAsync(data.Database);
    }

    internal static async Task RestoreV67Async(IMongoDatabase database)
    {
        var history = await HistoryAsync(database);
        if (history.Count <= 67) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(68, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var name in ProcurementReceivingSchemas.Collections) {
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"),
            ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            Assert.False(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.receiving(?:[.:]|$)"))).AnyAsync());
        // Only an empty newer owner may be removed from the isolated predecessor fixture.
        // M67 storage and genuine v1/v2 claims are neither rewritten nor removed.
        await ProcurementSettlementMigrationTests.RequireInstalledIndexesAsync(database);
        Assert.Equal(1, (await database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 68 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(67)), await HistoryAsync(database));
    }
    private static async Task RefusesAsync(IMongoDatabase database, UnitToko.Platform.IClock clock) =>
        Assert.Equal("Procurement receiving migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, clock).ApplyAsync())).Message);
    private static async Task RequireEmptyInstalledAsync(IMongoDatabase database) {
        foreach (var name in ProcurementReceivingSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementReceivingSchemas.Validator(name), ProcurementReceivingSchemas.Keys(name), CancellationToken.None);
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase database, string name) => await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase database) {
        var result = new BsonDocument(); foreach (var name in ProcurementReceivingSchemas.Collections.Concat(ProcurementSettlementSchemas.Collections).Append(BankTransactionClaims.Collection))
            result[name] = new BsonArray((await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)); return result;
    }
    private static async Task<BsonArray> CountersAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task RequireHighWaterAsync(IMongoDatabase database, BsonArray before) {
        var after = (await CountersAsync(database)).Select(row => row.AsBsonDocument).ToDictionary(row => row["_id"].AsString);
        foreach (var row in before) Assert.True(after[row["_id"].AsString]["value"].AsInt64 >= row["value"].AsInt64);
        foreach (var row in after.Values.Where(row => !before.Any(old => old["_id"] == row["_id"]))) {
            var highest = await database.GetCollection<BsonDocument>(row["_id"].AsString).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync();
            Assert.NotNull(highest); Assert.Equal(highest["_id"], row["value"]);
        }
    }
    private static async Task<BsonDocument> StateAsync(IMongoDatabase database, bool includeCounters = false) {
        var result = new BsonDocument();
        foreach (var name in ReceivingAccountSchemas.Collections.Concat(BankStatementSchemas.Collections).Concat(BankReceiptSchemas.Collections)
            .Concat(RefundPayoutSchemas.Collections).Concat(RefundSettlementSchemas.Collections).Concat(ProcurementDisbursementSchemas.Collections).Concat(ProcurementSettlementSchemas.Collections)
            .Concat(PurchaseOrderSchemas.Collections).Concat(ProcurementFundingSchemas.Collections).Concat(SupplierBankSchemas.Collections)
            .Concat(new[] { "bankTransactionClaims", "idempotencyRecords", "auditEvents", "outboxEvents", "journalEntries", "accountingAuditEvents", "accountingBooks", "accountingChartVersions", "periods", "evidencePins", "evidencePinAudits", "documentVersions", "evidenceValidationResults" }).Distinct(StringComparer.Ordinal))
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        if (includeCounters) result["idCounters"] = await CountersAsync(database); return result;
    }
}

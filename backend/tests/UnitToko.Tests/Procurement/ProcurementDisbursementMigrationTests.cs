using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementDisbursementMigrationTests(MongoFixture fixture)
{
    // These retained-history checks also assert the actual v67 bank-claim index transition.
    private static async Task UpgradeAsync(IMongoDatabase database, IClock clock) {
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        var gate = new MigrationGate(durable, clock, 67);
        IMigrationStep[] steps = [new ProcurementDisbursementMigration(durable, clock), new ProcurementSettlementMigration(durable, clock)];
        foreach (var step in steps) {
            var lease = await gate.AcquireAsync(step.Version, default);
            if (lease is null) continue;
            try { await step.ApplyAsync(lease, default); await lease.CompleteAsync(default); }
            catch { await lease.ReleaseAsync(); throw; }
        }
    }
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Actual_verified_refund_debit_upgrades_with_preserved_pin_claim_journal_and_rejects_missing_retained_proof_before_DDL()
    {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        const string key = "actual-refund-debit-before66";
        var saved = await data.VerifyAsync(await data.PreviewAsync(), key);
        Assert.Equal("bank_debit_verified", saved["state"].AsString);
        var settlement = await data.Rows(RefundSettlementBson.Events).Find(new BsonDocument()).SingleAsync();
        var debit = await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "debit")).SingleAsync();
        Assert.Equal(BankTransactionClaims.Debit(settlement), debit);
        Assert.True(await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "credit")).AnyAsync());
        Assert.Equal(data.PayoutRecord["pinId"], (await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "RefundPayout")).SingleAsync())["_id"]);
        // Current-runtime recovery and protected bytes are proved before the historical predecessor.
        // RetainedAsync then preserves their exact money, owner, pin, claim, journal and effect rows.
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
        await using (var original = await data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, data.PayoutId,
            data.Payout.Reservation.ReservationId, data.Payout.Reference)) {
            using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Payout.Bytes, bytes.ToArray());
        }
        await RestoreV65Async(data.Database);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        foreach (var name in new[] { RefundSettlementBson.Events, "evidencePins" }) {
            var definition = await DefinitionAsync(data.Database, name);
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name },
                { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], guard }) },
                { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        var definitions = await DefinitionsAsync(data.Database);
        var indexes = await IndexesAsync();
        var retained = await RetainedAsync();
        var history = await HistoryAsync(data.Database);
        foreach (var (name, filter) in new[] {
            (RefundSettlementBson.Events, new BsonDocument("_id", settlement["_id"])),
            (BankTransactionClaims.Collection, new BsonDocument("_id", debit["_id"])),
            ("accountingAuditEvents", new BsonDocument("journalId", settlement["journalId"])) }) {
            var actual = await data.Rows(name).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(name).DeleteOneAsync(filter)).DeletedCount);
            try {
                var damaged = await RetainedAsync();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeAsync(data.Database, data.Clock));
                Assert.Equal("Procurement disbursement migration preflight failed", error.Message);
                Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync());
                Assert.Equal(damaged, await RetainedAsync());
                Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(65)));
                Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 66 }, { "state", "applied" } }).AnyAsync());
                Assert.False(await data.Rows(name).Find(filter).AnyAsync());
            } finally {
                // Restore only the exact test-deleted producer row, never manufacture source history.
                await data.Rows(name).InsertOneAsync(actual);
            }
        }
        Assert.Equal(retained, await RetainedAsync());
        await UpgradeAsync(data.Database, data.Clock);
        Assert.Equal(retained, await RetainedAsync());
        var upgradedIndexes = await IndexesAsync();
        foreach (var entry in indexes.Where(entry => entry.Name != BankTransactionClaims.Collection)) Assert.Equal(entry.Value, upgradedIndexes[entry.Name]);
        await ProcurementSettlementMigrationTests.RequireInstalledIndexesAsync(data.Database);
        var upgraded = await HistoryAsync(data.Database);
        Assert.Equal(history, new BsonArray(upgraded.Take(65)));
        Assert.Equal(67, upgraded.Count); Assert.Equal("applied", upgraded.Single(row => row["_id"] == 66)["state"].AsString);
        Assert.Equal("applied", upgraded[^1]["state"].AsString);
        var after = await DefinitionsAsync(data.Database); Assert.Equal(definitions.Count, after.Count);
        foreach (var before in definitions) {
            var name = before["name"].AsString;
            var expected = before.DeepClone().AsBsonDocument;
            if (ProcurementDisbursementEvidenceSchemas.Collections.Contains(name))
                expected["options"]["validator"] = ProcurementDisbursementEvidenceSchemas.Extend(name, before["options"]["validator"].AsBsonDocument);
            if (name == BankTransactionClaims.Collection)
                expected["options"]["validator"] = ProcurementBankClaimSchemas.Extend(before["options"]["validator"].AsBsonDocument);
            Assert.Equal(expected, Assert.Single(after, row => row["name"] == name));
        }
        foreach (var name in ProcurementDisbursementSchemas.Collections) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        await UpgradeAsync(data.Database, data.Clock);
        Assert.Equal(upgraded, await HistoryAsync(data.Database)); Assert.Equal(retained, await RetainedAsync());
        async Task<BsonDocument> RetainedAsync() {
            var result = await PreservedAsync(data.Database);
            var names = ReceivingAccountSchemas.Collections.Concat(BankStatementSchemas.Collections).Concat(BankReceiptSchemas.Collections)
                .Concat(RefundPayoutSchemas.Collections).Concat(RefundSettlementSchemas.Collections).Concat(EvidenceAccessSchemas.Collections)
                .Concat(new[] { PaymentAllocationBson.Receipts, PaymentAllocationBson.Invoices, PaymentAllocationBson.Events,
                    ReturnFinancialApprovalBson.Events, "accountingBooks", "accountingChartVersions", "periods", "accountingAuditEvents", "accountingPeriodHistory" });
            foreach (var name in names.Distinct(StringComparer.Ordinal))
                result[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return result;
        }
        async Task<BsonDocument> IndexesAsync() {
            var result = new BsonDocument();
            foreach (var name in ProcurementDisbursementEvidenceSchemas.Collections.Concat(ProcurementDisbursementSchemas.Collections)
                .Concat(RefundSettlementSchemas.Collections).Append(BankTransactionClaims.Collection).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                result[name] = new BsonArray((await (await data.Rows(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal));
            return result;
        }
    }

    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Empty_upgrade_preserves_actual_payment_original_pin_history_and_stronger_storage_guards()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var submission = await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId,
            data.Reference, EvidenceAdmissionTestData.Meta("payment-retained-by66"));
        // Validate the protected original and original-key replay while the current schema is installed.
        // PreservedAsync below compares the exact submission, pin, validation and idempotency history.
        await using (var read = await data.OpenAsync()) { using var bytes = new MemoryStream(); await read.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray()); }
        Assert.Equal(submission, await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId,
            data.Reference, EvidenceAdmissionTestData.Meta("payment-retained-by66")));
        await RestoreV65Async(data.Database);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        foreach (var name in new[] { "evidencePins", ProcurementDisbursementTestData.Records }) {
            var definition = await DefinitionAsync(data.Database, name);
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name },
                { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        await data.Rows(ProcurementDisbursementTestData.Records).Indexes.DropOneAsync("ux_" + ProcurementDisbursementTestData.Records + "_0");
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database); var history = await HistoryAsync(data.Database);
        await UpgradeAsync(data.Database, data.Catalog.Clock);
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(65))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        var after = await DefinitionsAsync(data.Database);
        var changed = ProcurementDisbursementSchemas.Collections.Concat(ProcurementDisbursementEvidenceSchemas.Collections).Append(BankTransactionClaims.Collection).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(before.Where(row => !changed.Contains(row["name"].AsString)), after.Where(row => !changed.Contains(row["name"].AsString)));
        foreach (var name in changed) {
            var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(Assert.Single(before, row => row["name"] == name)["info"]["uuid"], current["info"]["uuid"]);
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var expected = name == BankTransactionClaims.Collection ? ProcurementBankClaimSchemas.Validator() :
                ProcurementDisbursementEvidenceSchemas.Collections.Contains(name) ? ProcurementDisbursementEvidenceSchemas.Validator(name) : ProcurementDisbursementSchemas.Validator(name);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, expected));
        }
        foreach (var name in ProcurementDisbursementSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in ProcurementDisbursementSchemas.Keys(name)) {
                var index = Assert.Single(indexes, row => row["key"] == key); Assert.True(index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean);
            }
        }
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, "evidencePins"))["options"]["validator"].AsBsonDocument, guard));
        var applied = await HistoryAsync(data.Database); await UpgradeAsync(data.Database, data.Catalog.Clock);
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database));
    }

    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("row")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")] [InlineData("retained_funding")]
    public async Task Unsafe_partial_disbursement_or_retained_funding_storage_refuses_before_any_DDL(string fault)
    {
        await using var data = await SupplierTestData.CreateAsync(fixture); await RestoreV65Async(data.Database);
        const string records = "procurementDisbursementRecords";
        if (fault == "row") await data.Rows(records).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(records).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", records }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "procurement.disbursement.record" } }, new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "retained_funding") await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", ProcurementFundingTestData.Decisions }, { "validationLevel", "off" } });
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        if (fault == "retained_funding") Assert.Equal("inventory_storage_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => UpgradeAsync(data.Database, data.Clock))).Code);
        else Assert.Equal("Procurement disbursement migration preflight failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeAsync(data.Database, data.Clock))).Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 66 }, { "state", "applied" } }).AnyAsync());
    }

    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Missing_actual_payment_pin_audit_refuses_evidence_extension_before_owner_or_shared_DDL()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("payment-before66-corruption"));
        await RestoreV65Async(data.Database); Assert.Equal(1, (await data.Rows("evidencePinAudits").DeleteManyAsync(new BsonDocument())).DeletedCount);
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        Assert.Equal("evidence_validation_integrity", (await Assert.ThrowsAsync<PersistenceConflictException>(() => UpgradeAsync(data.Database, data.Catalog.Clock))).Code);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 66 }, { "state", "applied" } }).AnyAsync());
    }

    internal static async Task RestoreV65Async(IMongoDatabase database, bool restoreSharedValidators = true)
    {
        await ProcurementSettlementMigrationTests.RestoreV66Async(database);
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var history = await HistoryAsync(database);
        if (history.Count == 65) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(66, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        Assert.Equal(new[] { 66 }, history.Skip(65).Select(row => row["_id"].AsInt32));
        foreach (var name in ProcurementDisbursementSchemas.Collections) {
            Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            foreach (var prefix in new[] { @"^procurement\.disbursement(?:[.:]|$)", @"^evidence\.procurement-disbursement-image(?:[.:]|$)" })
                Assert.False(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(prefix))).AnyAsync());
        Assert.False(await Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementDisbursement")).AnyAsync());
        foreach (var name in new[] { "evidenceLineages", "evidenceUploadReservations", "evidenceReadLeases" })
            Assert.False(await Rows(name).Find(new BsonDocument("target.kind", "ProcurementDisbursement")).AnyAsync());
        // Isolated fixture only: remove the exact accepted v66 wrapper, retaining all operator guards and all earlier history.
        foreach (var name in ProcurementDisbursementEvidenceSchemas.Collections) {
            var definition = await DefinitionAsync(database, name); var found = false;
            BsonDocument Previous(BsonDocument row) {
                if (CatalogSchemas.SemanticEquals(row, ProcurementDisbursementEvidenceSchemas.Validator(name))) { found = true; return ProcurementDisbursementEvidenceSchemas.PreviousValidator(name); }
                var copy = row.DeepClone().AsBsonDocument;
                if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray) copy["$and"] = new BsonArray(terms.AsBsonArray.Select(value => value.IsBsonDocument ? Previous(value.AsBsonDocument) : value.DeepClone()));
                return copy;
            }
            var previous = Previous(definition["options"]["validator"].AsBsonDocument); Assert.True(found);
            Assert.False(await Rows(name).Find(new BsonDocument("$nor", new BsonArray { previous })).AnyAsync());
            if (restoreSharedValidators)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", previous }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        Assert.Equal(1, (await Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 66 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(65)), await HistoryAsync(database));
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase db) {
        var result = new BsonDocument();
        foreach (var name in new[] { "organizationProfiles", "authorizationHistory", "skus", "variantDefinitions", "idempotencyRecords", "auditEvents", "outboxEvents", "paymentSubmissions", "evidencePins", "evidencePinAudits", "evidenceUploadReservations", "documentVersions", "evidenceValidationResults", "bankReceipts", "bankTransactionClaims", "journalEntries" }.Concat(ProcurementFundingSchemas.Collections).Concat(SupplierBankSchemas.Collections).Concat(ProcurementDisbursementSchemas.Collections))
            result[name] = new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}

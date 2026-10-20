using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using R = UnitToko.Platform.Procurement.ProcurementReceivingBson;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementInspectionMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Actual_submitted_image_original_and_pin_survive_video_extension_but_missing_prior_proof_refuses_before_DDL()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var metadata = EvidenceAdmissionTestData.Meta("payment-before69");
        var submitted = await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, metadata);
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument("_id", submitted["submissionId"])).SingleAsync();
        await RestoreV68Async(data.Database);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await StrengthenAsync(data.Database, "evidencePins", guard);
        var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        var history = await HistoryAsync(data.Database); var priorHashes = PreviousSchemaHashes();
        foreach (var (name, filter) in new[] {
            ("evidencePinAudits", new BsonDocument("pinId", submission["pinId"])),
            ("idempotencyRecords", new BsonDocument { { "operation", "evidence.payment-image.complete" }, { "evidenceAdmission.reservationId", data.Reservation.ReservationId } }) }) {
            var exact = await data.Rows(name).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(name).DeleteOneAsync(new BsonDocument("_id", exact["_id"]))).DeletedCount);
            try { await RefusesWithoutChangesAsync(data.Database, data.Catalog.Clock,
                name == "idempotencyRecords" ? "Evidence migration found incompatible missing relation in admission graph; review the data before retrying" : null); }
            finally { await data.Rows(name).InsertOneAsync(exact); }
            Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
            Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(68)));
        }
        var retained = await StateAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        await RequireExpectedDefinitionsAsync(data.Database, definitions); Assert.Equal(priorHashes, PreviousSchemaHashes());
        await RequireEmptyInstalledAsync(data.Database);
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(68)));
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, "evidencePins"))["options"]["validator"].AsBsonDocument, guard));
        await using (var original = await data.OpenAsync()) {
            using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        Assert.Equal(submitted, await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, metadata));
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Actual_zero_advance_order_notice_and_container_history_survive_while_missing_head_or_intake_receipt_denies_upgrade()
    {
        await using var data = await ProcurementReceivingTestData.CreateAsync(fixture, onReceipt: true);
        var notice = await data.NoticeAsync("notice-before69"); var noticeId = notice["noticeId"].AsInt64;
        var intake = await data.IntakeAsync(noticeId, key: "intake-before69");
        await RestoreV68Async(data.Database);
        var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        foreach (var (name, filter) in new[] {
            (R.Heads, new BsonDocument("_id", noticeId)),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Receiver.UserId }, { "operation", R.IntakeOperation }, { "key", "intake-before69" } }) }) {
            var exact = await data.Rows(name).Find(filter).SingleAsync();
            Assert.Equal(1, (await data.Rows(name).DeleteOneAsync(new BsonDocument("_id", exact["_id"]))).DeletedCount);
            try { await RefusesWithoutChangesAsync(data.Database, data.Clock); }
            finally { await data.Rows(name).InsertOneAsync(exact); }
        }
        var retained = await StateAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        await RequireExpectedDefinitionsAsync(data.Database, definitions); await RequireEmptyInstalledAsync(data.Database);
        Assert.Equal(new BsonArray(history.Take(68)), new BsonArray((await HistoryAsync(data.Database)).Take(68)));
        Assert.Equal(notice, await data.Service.LookupAsync(data.Maker, R.NoticeOperation, "notice-before69"));
        Assert.Equal(intake, await data.Service.LookupAsync(data.Receiver, R.IntakeOperation, "intake-before69"));
        Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", R.Heads)).AnyAsync());
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_partial_video_install_preserves_stronger_guards_rejects_incompatible_storage_or_orphan_receipts_and_never_seeds_policy()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV68Async(data.Database);
        var extra = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        await StrengthenAsync(data.Database, B.Drafts, extra); await StrengthenAsync(data.Database, ProcurementInspectionEvidenceSchemas.ReadPolicies, extra);
        var rows = data.Database.GetCollection<BsonDocument>(B.Drafts);
        await rows.Indexes.DropOneAsync("ux_" + B.Drafts + "_0");
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("noticeId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_inspection_partial", Unique = true, PartialFilterExpression = new BsonDocument("version", 1L) }));
        try { await RefusesWithoutChangesAsync(data.Database, data.Clock); }
        finally { await rows.Indexes.DropOneAsync("unknown_inspection_partial"); }
        var orphan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "unrelated.test" },
            { "processingReceipt", new BsonDocument("evidenceVideoValidation", true) } };
        await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(orphan, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefusesWithoutChangesAsync(data.Database, data.Clock); }
        finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        var counter = new BsonDocument { { "_id", B.Heads }, { "value", 0L } };
        await data.Database.GetCollection<BsonDocument>("idCounters").InsertOneAsync(counter);
        try { await RefusesWithoutChangesAsync(data.Database, data.Clock); }
        finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", B.Heads))).DeletedCount); }
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection }, { "validationLevel", "off" } });
        try { await RefusesWithoutChangesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection }, { "validationLevel", "strict" } }); }
        var definitions = await DefinitionsAsync(data.Database); var retained = await StateAsync(data.Database); var priorHashes = PreviousSchemaHashes();
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(retained, await StateAsync(data.Database)); await RequireExpectedDefinitionsAsync(data.Database, definitions);
        Assert.Equal(priorHashes, PreviousSchemaHashes()); await RequireEmptyInstalledAsync(data.Database);
        foreach (var name in new[] { B.Drafts, ProcurementInspectionEvidenceSchemas.ReadPolicies })
            Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, name))["options"]["validator"].AsBsonDocument, extra));
        var accepted = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database); var history = await HistoryAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(accepted, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database)); Assert.Equal(history, await HistoryAsync(data.Database));
        var malformed = new BsonDocument { { "_id", 99L }, { "schemaVersion", 1 }, { "storeId", new string('a', 64) }, { "active", true } };
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Database.GetCollection<BsonDocument>(ProcurementInspectionEvidenceSchemas.ReadPolicies).InsertOneAsync(malformed))).WriteError.Code);
        await RequireEmptyInstalledAsync(data.Database);
    }

    internal static async Task RestoreV68Async(IMongoDatabase database)
    {
        await SupplierDocumentMigrationTests.RestoreV69Async(database);
        var history = await HistoryAsync(database);
        if (history.Count <= 68) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(69, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var name in NewCollections) {
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            foreach (var prefix in new[] { @"^procurement\.inspection(?:[.:]|$)", @"^evidence\.procurement-unboxing-video(?:[.:]|$)", @"^evidence\.video-validation(?:[.:]|$)" })
                Assert.False(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(prefix))).AnyAsync());
        Assert.False(await database.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("processingReceipt.evidenceVideoValidation", new BsonDocument("$exists", true))).AnyAsync());
        foreach (var name in new[] { "evidenceLineages", "evidenceUploadReservations", "evidenceReadLeases" })
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument("target.kind", "ProcurementInspection")).AnyAsync());
        Assert.False(await database.GetCollection<BsonDocument>("evidencePins").Find(new BsonDocument("associationKind", "ProcurementInspection")).AnyAsync());
        foreach (var name in ProcurementInspectionEvidenceSchemas.Collections) {
            var definition = await DefinitionAsync(database, name); var found = false;
            BsonDocument Prior(BsonDocument value) {
                if (CatalogSchemas.SemanticEquals(value, ProcurementInspectionEvidenceSchemas.Validator(name))) { found = true; return ProcurementInspectionEvidenceSchemas.PreviousValidator(name); }
                var copy = value.DeepClone().AsBsonDocument;
                if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                    copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Prior(term.AsBsonDocument) : term.DeepClone()));
                return copy;
            }
            var previous = Prior(definition["options"]["validator"].AsBsonDocument); Assert.True(found);
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument("$nor", new BsonArray { previous })).AnyAsync());
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", previous }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        await ProcurementSettlementMigrationTests.RequireInstalledIndexesAsync(database);
        Assert.Equal(1, (await database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 69 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(68)), await HistoryAsync(database));
    }

    private static readonly string[] NewCollections = [.. ProcurementInspectionSchemas.Collections, .. ProcurementInspectionEvidenceSchemas.NewCollections];
    private static async Task RefusesWithoutChangesAsync(IMongoDatabase database, IClock clock, string? expectedMessage = null) {
        var definitions = await DefinitionsAsync(database); var indexes = await IndexesAsync(database); var retained = await StateAsync(database, true);
        var prior = new BsonArray((await HistoryAsync(database)).Take(68));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, clock).ApplyAsync());
        if (expectedMessage is null) Assert.StartsWith("Procurement inspection", error.Message);
        else Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(definitions, await DefinitionsAsync(database)); Assert.Equal(indexes, await IndexesAsync(database)); Assert.Equal(retained, await StateAsync(database, true));
        Assert.Equal(prior, new BsonArray((await HistoryAsync(database)).Take(68)));
        Assert.False(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 69 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task RequireEmptyInstalledAsync(IMongoDatabase database) {
        foreach (var name in NewCollections) {
            var owner = ProcurementInspectionSchemas.Collections.Contains(name);
            await InventoryRepository.RequireStorageAsync(database, name, owner ? ProcurementInspectionSchemas.Validator(name) : ProcurementInspectionEvidenceSchemas.Validator(name),
                owner ? ProcurementInspectionSchemas.Keys(name) : [ProcurementInspectionEvidenceSchemas.Key(name)], CancellationToken.None);
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static BsonDocument PreviousSchemaHashes() {
        var result = new BsonDocument();
        foreach (var name in ProcurementInspectionEvidenceSchemas.Collections) {
            var previous = ProcurementInspectionEvidenceSchemas.PreviousValidator(name); var next = ProcurementInspectionEvidenceSchemas.Validator(name);
            var retained = name == "outboxEvents" ? next["$and"][0]["$or"][0] : next["$or"][0];
            Assert.Equal(previous, retained); result[name] = EvidenceValidationRecords.Hash(previous);
        }
        Assert.Equal(10 * 1024 * 1024L, EvidenceAccessSchemas.Validator("evidenceReadPolicies")["$and"][0]["$jsonSchema"]["properties"]["maximumBytes"]["maximum"].AsInt64);
        Assert.Equal(EvidenceSchemas.Validator("evidenceStorePolicies"), ProcurementInspectionEvidenceSchemas.Validator(ProcurementInspectionEvidenceSchemas.StorePolicies));
        Assert.Equal(BankTransactionClaimSchemas.Validator(), ProcurementBankClaimSchemas.Validator()["$or"][0]);
        return result;
    }
    internal static async Task RequireExpectedDefinitionsAsync(IMongoDatabase database, List<BsonDocument> before) {
        var after = await DefinitionsAsync(database); Assert.Equal(before.Count, after.Count);
        foreach (var prior in before) {
            var name = prior["name"].AsString; var expected = prior.DeepClone().AsBsonDocument;
            if (ProcurementInspectionEvidenceSchemas.Collections.Contains(name)) expected["options"]["validator"] = SupplierDocumentEvidenceSchemas.Extend(name,
                ProcurementInspectionEvidenceSchemas.Extend(name, prior["options"]["validator"].AsBsonDocument));
            Assert.Equal(expected, Assert.Single(after, row => row["name"] == name));
        }
    }
    private static async Task StrengthenAsync(IMongoDatabase database, string name, BsonDocument guard) =>
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", new BsonDocument("$and", new BsonArray {
            (await DefinitionAsync(database, name))["options"]["validator"], guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase database, string name) => await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase database) {
        var result = new BsonDocument(); foreach (var name in NewCollections.Concat(ProcurementInspectionEvidenceSchemas.Collections).Concat(ProcurementReceivingSchemas.Collections).Append(BankTransactionClaims.Collection))
            result[name] = new BsonArray((await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)); return result;
    }
    private static async Task<BsonDocument> StateAsync(IMongoDatabase database, bool counters = false) {
        var result = new BsonDocument();
        foreach (var name in EvidenceSchemas.Collections.Concat(EvidenceAccessSchemas.Collections).Concat(EvidenceValidationSchemas.Collections).Concat(PaymentSubmissionSchemas.Collections)
            .Concat(ProcurementReceivingSchemas.Collections).Concat(NewCollections).Concat(new[] { BankTransactionClaims.Collection, "journalEntries", "accountingAuditEvents", "payments", "stockMovements" }).Distinct(StringComparer.Ordinal))
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        if (counters) result["idCounters"] = new BsonArray(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return result;
    }
}

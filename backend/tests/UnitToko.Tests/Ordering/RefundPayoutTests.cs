using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using Xunit.Abstractions;
using B = UnitToko.Platform.Billing.RefundPayoutBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundPayoutTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Actual_draft_image_and_record_recover_the_exact_lost_key_with_one_pin_and_no_cash_or_money_effect() {
        var stages = new List<string>(); var whole = Stopwatch.StartNew();
        var commands = new ConcurrentDictionary<string, (int Count, double Milliseconds)>();
        fixture.CommandCompleted = (name, duration) => commands.AddOrUpdate(name, (1, duration), (_, previous) => (previous.Count + 1, previous.Milliseconds + duration));
        void CommandTiming(string phase) {
            var snapshot = commands.ToArray(); foreach (var pair in snapshot) commands.TryRemove(pair.Key, out _);
            stages.Add(JsonSerializer.Serialize(new { stage = "commands/" + phase, count = snapshot.Sum(pair => pair.Value.Count),
                elapsedMs = snapshot.Sum(pair => pair.Value.Milliseconds),
                top = snapshot.OrderByDescending(pair => pair.Value.Milliseconds).Take(8).Select(pair => new { name = pair.Key, count = pair.Value.Count, elapsedMs = pair.Value.Milliseconds }) }));
        }
        async Task<T> Timed<T>(string stage, Func<Task<T>> action) {
            var started = Stopwatch.GetTimestamp();
            try {
                var result = await action();
                stages.Add(JsonSerializer.Serialize(new { stage, status = "completed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }));
                CommandTiming(stage);
                return result;
            } catch (Exception error) {
                stages.Add(JsonSerializer.Serialize(new { stage, status = "failed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, errorType = error.GetType().Name }));
                CommandTiming(stage);
                throw;
            }
        }
        Task Step(string stage, Func<Task> action) => Timed(stage, async () => { await action(); return true; });
        try {
            await using var data = await Timed("fixture", () => Data.CreateAsync(fixture, createDraft: false));
            data.PhaseObserver = (phase, elapsedMs) => {
                stages.Add(JsonSerializer.Serialize(new { stage = "source/" + phase, status = "completed", elapsedMs }));
                CommandTiming("source/" + phase);
            };
            data.Draft = await Timed("draft", () => data.CreateDraftAsync("payout-draft")); var original = await data.PreservedAsync();
            Assert.Equal(data.Draft, await Timed("draft-recovery", () => data.Service.RecoverDraftAsync(data.Actor, data.ApprovalId, "payout-draft")));
            await Step("upload", data.UploadAsync); await Step("validate", () => data.ValidateAsync());
            var preview = await Timed("preview", data.PreviewRecordAsync); var metadata = Data.Meta("payout-record-lost-reply");
            var saved = await Timed("record", () => data.RecordAsync(preview, metadata)); // The caller discards this reply and uses its retained exact key.
            Assert.Equal(saved, await Timed("record-recovery", () => data.Service.RecoverRecordAsync(data.Actor, data.DraftId, metadata.IdempotencyKey)));
            Assert.Equal(saved, await Timed("record-replay", () => data.RecordAsync(preview, metadata)));
            Assert.Equal("recorded_unverified", saved["state"]); Assert.Equal(600m, saved["amount"].AsDecimal);
            var payout = await data.Rows(B.Payouts).Find(new BsonDocument()).SingleAsync();
            var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "RefundPayout")).SingleAsync();
            Assert.Equal(payout["pinId"], pin["_id"]); Assert.Equal(payout["_id"], pin["associationId"]); Assert.Equal(data.DraftId, pin["refundPayoutDraftId"].AsInt64);
            Assert.Equal(payout["snapshotHash"], pin["associationHash"]); Assert.Equal(data.Reference!.Sha256, pin["reference"]["sha256"].AsString);
            Assert.False(pin.Contains("declarationId")); Assert.False(pin.Contains("reviewRevision"));
            Assert.Equal(1, await data.Rows("evidencePinAudits").CountDocumentsAsync(new BsonDocument("pinId", pin["_id"])));
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, B.Operation)));
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", B.Operation)).SingleAsync();
            Assert.Equal(PersonApprovalReceipt.Create(Permissions.DisbursementRecord, new(1, 1, ApprovedBy: data.Source.Approver.UserId)), receipt[PersonApprovalReceipt.Field]);
            Assert.Equal(original, await data.PreservedAsync());
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RecordAsync(preview, metadata with { Reason = "A different transfer evidence reason" }));
            Assert.Null(await data.Service.RecoverRecordAsync(data.Actor, data.DraftId, "missing-original-key"));
        } finally {
            fixture.CommandCompleted = null;
            stages.Add(JsonSerializer.Serialize(new { stage = "total", elapsedMs = whole.Elapsed.TotalMilliseconds }));
            foreach (var line in stages) output.WriteLine(line);
            // Persist only stage/status/duration/type, never account details, keys or originals.
            try {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) directory = directory.Parent;
                if (directory is null) throw new InvalidOperationException("The authoritative checkout was not found");
                var folder = Path.Combine(directory.FullName, "tmp", "production", "ocr-results"); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "refund-payout-flow-stage-" + Guid.NewGuid().ToString("N") + ".jsonl");
                await File.WriteAllLinesAsync(path, stages); output.WriteLine("STAGE_TRACE " + path);
            } catch (Exception traceError) { output.WriteLine("STAGE_TRACE_WRITE_FAILED " + traceError.GetType().Name); }
        }
    }
    [WindowsStorageFact]
    public async Task Actual_approver_and_a_linked_equivalent_recorder_cannot_record_an_independent_payout() {
        await using var data = await Data.CreateAsync(fixture); var original = await data.PreservedAsync();
        await data.Catalog.Base.AddGrantAsync(data.Source.Approver, 987541, CatalogAuthoringTestData.Shop, [Permissions.DisbursementRecord]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDraftAsync(data.Source.Approver, data.ApprovalId, data.Input));
        var maker = data.Actor.UserId; var approver = data.Source.Approver.UserId;
        await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(maker)),
                Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(approver)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(approver)),
                Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(approver), PersonTestData.PersonId(maker) })
                    .Set("memberUserIds", new BsonArray { approver, maker }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(maker)),
                Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
        });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDraftAsync(data.Actor, data.ApprovalId, data.Input));
        Assert.Equal(0, await data.Rows(B.Payouts).CountDocumentsAsync(new BsonDocument())); Assert.Equal(original, await data.PreservedAsync());
    }
    [WindowsStorageFact]
    public async Task Two_real_drafts_for_one_reserved_refund_can_commit_only_one_record_and_one_outgoing_pin() {
        await using var data = await Data.CreateAsync(fixture); var original = await data.PreservedAsync();
        await data.UploadAsync(); await data.ValidateAsync(); var first = await data.PreviewRecordAsync();
        var secondDraft = await data.CreateDraftAsync("payout-second-draft", "SYNTHETIC-SECOND-TRANSFER"); var secondId = secondDraft["draftId"].AsInt64;
        var second = await data.UploadForAsync(secondId, "payout-second-image"); await data.ValidateAsync();
        var secondPreview = await data.Service.PreviewRecordAsync(data.Actor, secondId, 1, second.Reservation.ReservationId, second.Reference);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.RecordAsync(first, Data.Meta("payout-race-one"))),
            Record.ExceptionAsync(() => data.Evidence.RecordAsync(data.Actor, secondId, 1, second.Reservation.ReservationId, second.Reference, secondPreview.Binding, Data.Meta("payout-race-two"))));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        Assert.Equal(1, await data.Rows(B.Payouts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
        Assert.Equal(1, await data.Rows(B.Drafts).CountDocumentsAsync(new BsonDocument("recordedPayoutId", new BsonDocument("$type", "long"))));
        Assert.Equal(original, await data.PreservedAsync());
    }
    [WindowsStorageFact]
    public async Task Required_pin_receipt_and_audit_denials_roll_back_all_payout_effects_and_the_exact_key_can_retry() {
        await using var data = await Data.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var original = await data.PreservedAsync(); var preview = await data.PreviewRecordAsync(); var metadata = Data.Meta("payout-atomic-retry");
        foreach (var (table, field, value) in new[] { ("evidencePins", "associationKind", "RefundPayout"), ("idempotencyRecords", "operation", B.Operation), ("auditEvents", "kind", B.Operation) }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", value)) }) },
                    { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.RecordAsync(preview, metadata)));
                Assert.Equal(0, await data.Rows(B.Payouts).CountDocumentsAsync(new BsonDocument()));
                Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
                foreach (var (effect, kind) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                    Assert.Equal(0, await data.Rows(effect).CountDocumentsAsync(new BsonDocument(kind, B.Operation)));
                Assert.True((await data.Rows(B.Drafts).Find(new BsonDocument("_id", data.DraftId)).SingleAsync())["recordedPayoutId"].IsBsonNull);
                Assert.Equal(original, await data.PreservedAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var saved = await data.RecordAsync(preview, metadata); Assert.Equal(saved, await data.Service.RecoverRecordAsync(data.Actor, data.DraftId, metadata.IdempotencyKey));
        Assert.Equal(original, await data.PreservedAsync());
    }
    internal sealed class Data(ReturnFinancialMixedHistoryTests.Data source) : IAsyncDisposable {
        // The authority helper's provenance requester/reviewer are distinct from the
        // reviewed beneficiary participants; neither participant reviews their own record.
        internal const long BeneficiaryProposerId = 7681, BeneficiaryReviewerId = 7682;
        internal ReturnFinancialMixedHistoryTests.Data Source { get; } = source;
        internal IMongoDatabase Database => Source.Database;
        internal CatalogAuthoringTestData Catalog => Source.Allocation.Source.Read.Catalog;
        internal UnitToko.Platform.IClock Clock => Source.Clock;
        internal ActorContext Actor => Source.Allocation.Source.Reader;
        internal RefundPayoutInput Input = null!;
        internal long ApprovalId;
        internal BsonDocument Draft = null!;
        internal long DraftId => Draft["draftId"].AsInt64;
        internal ReservationResult Reservation = null!;
        internal EvidenceRef Reference = null!;
        internal readonly byte[] Bytes = ImageDecoderTests.Png(13, 7);
        internal Action<string, double>? PhaseObserver;
        internal RefundPayoutService Service {
            get {
                var service = new RefundPayoutService(Database, new(Catalog.Client), new(Database), Clock, Source.Service, Source.Allocation.Source.Bank.Service, Source.Cases);
                service.PhaseObserver = PhaseObserver; return service;
            }
        }
        internal RefundPayoutEvidenceService Evidence => new(Database, new(Catalog.Client), new(Database), Clock, Service, Source.Allocation.Source.Read.Files.Registration);
        internal EvidenceService Core => new(Database, new(Catalog.Client), new(Database), Clock, Source.Allocation.Source.Read.Files.Registration, refundPayoutTargets: Service);
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key,
            "Synthetic recording of independently approved exact refund evidence; no transfer executed", "synthetic-refund-payout");
        internal static AuthorizationReadMetadata ReadMetadata => new("refund-payout-evidence-view", "synthetic-refund-private-original");

        internal static async Task<Data> CreateAsync(MongoFixture fixture, bool createDraft = true) {
            var source = await ReturnFinancialMixedHistoryTests.Data.CreateAsync(fixture);
            try {
                var data = new Data(source);
                var input = source.Input(1, 600m);
                var preview = await source.Service.PreviewAsync(source.Approver, source.CaseId, source.ReturnId, "refund", input);
                data.ApprovalId = (await source.Service.ApproveAsync(source.Approver, source.CaseId, source.ReturnId, "refund",
                    preview.Binding, input, Meta("payout-fixture-approval")))["financialApprovalId"].AsInt64;
                await data.Catalog.Base.AddGrantAsync(data.Actor, 987420, CatalogAuthoringTestData.Shop, [Permissions.DisbursementRecord]);
                foreach (var user in new[] { data.Actor.UserId, BeneficiaryProposerId, BeneficiaryReviewerId })
                    if (!await data.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(user))).AnyAsync())
                        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, user);
                var scope = new BsonDocument { { "fulfillmentOrganizationId", CatalogAuthoringTestData.Shop.OrganizationId },
                    { "shopId", CatalogAuthoringTestData.Shop.ShopId!.Value }, { "channelId", CatalogAuthoringTestData.Shop.ChannelId!.Value }, { "warehouseId", BsonNull.Value } };
                var bank = source.Allocation.Source.Bank.Statements.Selection;
                var policy = new BsonDocument { { "_id", (await new IdAllocator(data.Database).NextAsync(B.Policies)).Value }, { "schemaVersion", 1 },
                    { "revision", 1L }, { "fence", 0L }, { "scope", scope }, { "rule", RefundPayoutPolicy.ExactReservedRefund },
                    { "payingAccountId", bank.AccountId }, { "payingAccountVersionId", bank.AccountVersionId }, { "active", true },
                    { "approvedAt", data.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime } };
                foreach (var field in new[] { "beneficiaryVerificationProcedureReference", "payoutRecordingProcedureReference", "operatingOwnerReference", "approvalReference" })
                    policy[field] = "synthetic-test-only-" + field;
                await data.Rows(B.Policies).InsertOneAsync(policy);
                var people = await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) =>
                    new PersonRegistryService(data.Database, data.Clock).ResolveProofsAsync(tx, [BeneficiaryProposerId, BeneficiaryReviewerId], ct));
                var details = new ReceivingAccountDetails("BENEFICIARY", "009876543210", "Synthetic approved branch beneficiary", "IDR");
                var approval = await data.Rows(ReturnFinancialApprovalBson.Events).Find(new BsonDocument("_id", data.ApprovalId)).SingleAsync();
                var beneficiary = new BsonDocument { { "_id", (await new IdAllocator(data.Database).NextAsync(B.Beneficiaries)).Value }, { "schemaVersion", 1 },
                    { "revision", 1L }, { "fence", 0L }, { "scope", scope }, { "buyingOrganizationId", approval["party"]["buyingOrganizationId"] },
                    { "details", ReceivingAccountBson.Details(details) }, { "physicalIdentity", ReceivingAccountBson.PhysicalIdentity(details) },
                    { "method", "authenticated-bank-record" }, { "proposedBy", BeneficiaryProposerId }, { "verifiedBy", BeneficiaryReviewerId },
                    { "approvedBy", BeneficiaryReviewerId }, { "people", new BsonArray(people.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
                    { "active", true }, { "approvedAt", data.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime } };
                foreach (var field in new[] { "authorizationReference", "trustedSourceReference", "trustedChannelReference", "operatingOwnerReference", "approvalReference" })
                    beneficiary[field] = "synthetic-test-only-" + field;
                beneficiary["snapshotHash"] = B.Hash(beneficiary); await data.Rows(B.Beneficiaries).InsertOneAsync(beneficiary);
                data.Input = new(beneficiary["_id"].AsInt64, 1, bank.AccountId, bank.AccountVersionId, new(600m),
                    AccountingCalendar.BusinessDate(data.Clock.UtcNow), "SYNTHETIC-TRANSFER-REFERENCE");
                if (createDraft) data.Draft = await data.CreateDraftAsync("payout-draft");
                return data;
            } catch { await source.DisposeAsync(); throw; }
        }
        internal async Task<BsonDocument> CreateDraftAsync(string key, string? transferReference = null) {
            var input = transferReference is null ? Input : Input with { TransferReference = transferReference };
            var preview = await Service.PreviewDraftAsync(Actor, ApprovalId, input);
            return await Service.CreateDraftAsync(Actor, ApprovalId, preview.Binding, input, Meta(key));
        }
        internal async Task<(ReservationResult Reservation, EvidenceRef Reference)> UploadForAsync(long draftId, string key) {
            var reservation = await Evidence.ReserveAsync(Actor, draftId, 1, Meta(key + "-reserve"));
            Assert.Equal(reservation, await Evidence.RecoverReservationAsync(Actor, draftId, key + "-reserve"));
            using var bytes = new MemoryStream(Bytes);
            var uploaded = await Evidence.UploadAsync(Actor, draftId, reservation.ReservationId, 1, bytes, Meta(key + "-upload"));
            Assert.Equal("quarantined", uploaded.State); return (reservation, uploaded.Evidence!.Reference);
        }
        internal async Task UploadAsync() {
            var uploaded = await UploadForAsync(DraftId, "payout-image"); Reservation = uploaded.Reservation; Reference = uploaded.Reference;
        }
        internal async Task ValidateAsync(string status = "clean") {
            var processor = new EvidenceValidationProcessor(Database, new(Catalog.Client), new(Database), Clock, Source.Allocation.Source.Read.Files.Registration,
                EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
                new EvidenceValidationTests.FixtureScanner(status, signaturesAt: Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
            Assert.Equal("completed", await processor.RunOnceAsync());
        }
        internal Task<RefundPayoutDraftPreview> PreviewRecordAsync() => Service.PreviewRecordAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference);
        internal Task<BsonDocument> RecordAsync(RefundPayoutDraftPreview preview, AuthorizationCommandMetadata metadata) =>
            Evidence.RecordAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference, preview.Binding, metadata);
        internal async Task<BsonDocument> PreservedAsync() {
            var snapshots = new BsonDocument();
            foreach (var name in new[] { PaymentAllocationBson.Invoices, PaymentAllocationBson.Receipts, PaymentAllocationBson.Events,
                ReturnEntitlementBson.Requests, ReturnEntitlementBson.Pools, ReturnFinancialApprovalBson.Events, BankReceiptBson.Receipts, BankReceiptBson.Accounts,
                "journalEntries", "stockBalances", "lots", "movements", "reservations", "stockMovements", "invoices", "paymentSubmissions" }.Distinct()) {
                var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) {
                    row.Remove("fence"); if (name == "invoices") row.Remove("postingFence");
                }
                // Preserve every source hash, money/stock value and version; only the
                // existing read/write serialization fences may advance during access.
                snapshots[name] = new BsonArray(rows);
            }
            return snapshots;
        }
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}

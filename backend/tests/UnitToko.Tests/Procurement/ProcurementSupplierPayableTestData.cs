using System.Globalization;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Tests.Procurement;

// All source records use their real owners in one database and one approved PO/book.
// People, Finance approvals, observations and local scanner acceptance are synthetic test inputs only.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementSupplierPayableTestData(ProcurementStockValuationTestData valuation, MongoClient client, Action<string>? diagnostics = null) : IAsyncDisposable
{
    internal const string Reason = "Synthetic reviewed supplier payable and verified advance; no new payment or stock release";
    internal ProcurementStockValuationTestData Valuation { get; } = valuation;
    internal SupplierDocumentTestData Documents => Valuation.Documents;
    internal ProcurementReceivingTestData Receiving => Valuation.Inspection.Receiving;
    internal StoreTestData Base => Valuation.Base;
    internal IMongoDatabase Database => Valuation.Database;
    internal MongoUnitOfWork Unit => Valuation.Unit;
    internal MutableAuthorizationClock Clock => Valuation.Clock;
    internal CatalogAuthoringTestData Catalog { get; } = new(valuation.Base, client);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!, Recorder = null!, BankVerifier = null!;
    internal BsonDocument ApprovedValuation = null!, Policy = null!, Settlement = null!, Recorded = null!;
    internal ProcurementFundingTestData? Funding;
    internal SupplierBankTestData? Beneficiaries;
    internal BankReceiptTestData? Bank;
    internal long AllocationId => ApprovedValuation["allocationId"].AsInt64;
    internal long PolicyId => Policy["_id"].AsInt64;
    internal long SettlementId => Settlement["id"].AsInt64;
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal BankReceiptService BankService => new(Database, Unit, new(Database), Clock,
        new(Database, Unit, new(Database), Clock, new(Database, Unit, new(Database), Clock)));
    internal ProcurementDisbursementService Disbursements => new(Database, Unit, new(Database), Clock, Receiving.Procurement.Catalog,
        new(Database, Unit, new(Database), Clock, Receiving.Procurement.Catalog, Receiving.Orders.Service),
        new(Database, Unit, new(Database), Clock, Receiving.Procurement.Catalog), BankService);
    internal ProcurementSettlementService Settlements => new(Database, Unit, new(Database), Clock, Receiving.Procurement.Catalog, Disbursements, BankService);
    internal ProcurementSupplierPayableService Service => new(Database, Unit, new(Database), Clock, Valuation.Service, Settlements);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "synthetic-supplier-payable-test");
    internal ProcurementSupplierPayableInput Recognize() => new(ProcurementSupplierPayableKind.Recognize, ProcurementRequestTestData.Location,
        AllocationId, null, null, null, PolicyId, 1, AccountingCalendar.BusinessDate(Clock.UtcNow), Reason);
    internal ProcurementSupplierPayableInput Apply(long payableId, decimal amount = 4000) => new(ProcurementSupplierPayableKind.ApplyAdvance,
        ProcurementRequestTestData.Location, null, payableId, SettlementId, new Money(amount), PolicyId, 1, AccountingCalendar.BusinessDate(Clock.UtcNow), Reason);

    internal static async Task<ProcurementSupplierPayableTestData> CreateAsync(MongoFixture fixture, bool withAdvance = true, bool createPolicy = true, Action<string>? diagnostics = null,
        bool includeCustomerMappings = false)
    {
        var valuation = await ProcurementStockValuationTestData.CreateAsync(fixture, requiredAdvance: withAdvance ? 10000 : 0, includePayableMappings: true,
            includeCustomerMappings: includeCustomerMappings);
        var data = new ProcurementSupplierPayableTestData(valuation, fixture.Client, diagnostics);
        try {
            data.ApprovedValuation = await valuation.DecideAsync((await valuation.ProposeAsync("payable-source-valuation"))["proposalId"].AsInt64, "payable-source-valuation-approved");
            var scope = ProcurementRequestTestData.Scope;
            data.Maker = await data.Base.AddActorAsync(750, scope, Permissions.ProcurementRead, Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose);
            data.Checker = await data.Base.AddActorAsync(751, scope, Permissions.ProcurementRead, Permissions.ProcurementPayableRead, Permissions.ProcurementPayableDecide, Permissions.JournalPost);
            data.Reader = await data.Base.AddActorAsync(752, scope, Permissions.ProcurementRead, Permissions.ProcurementPayableRead);
            data.Outsider = await data.Base.AddActorAsync(753, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: data.Receiving.Procurement.ForeignShopId,
                WarehouseId: data.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.ProcurementRead,
                Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide, Permissions.JournalPost);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            if (withAdvance) await data.CreateVerifiedAdvanceAsync();
            if (createPolicy) await data.InstallPolicyAsync();
            Assert.False(await data.Rows(B.Proposals).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows(B.Payables).Find(new BsonDocument()).AnyAsync());
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }

    private async Task InstallPolicyAsync()
    {
        var chart = await Rows("accountingChartVersions").Find(new BsonDocument("_id", Valuation.Book["chartId"])).SingleAsync();
        var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveAsync(tx,
            [Valuation.BookMaker.UserId, Valuation.BookChecker.UserId], ct));
        Policy = new BsonDocument {
            { "_id", (await new IdAllocator(Database).NextAsync(B.Policies)).Value }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
            { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) }, { "currency", "IDR" },
            { "approvedAt", Clock.UtcNow.UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime },
            { "proposedBy", Valuation.BookMaker.UserId }, { "approvedBy", Valuation.BookChecker.UserId }, { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) },
            { "sourceTreatment", "receipt_accrual_to_payable" }, { "applicationTreatment", "verified_advance_to_payable" },
            { "bookId", Valuation.Book["bookId"] }, { "chartVersionId", Valuation.Book["chartId"] },
            { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER-NOT-LIVE-ACCEPTANCE" }, { "approvalReference", "SYNTHETIC-INDEPENDENT-PAYABLE-POLICY" }
        };
        foreach (var (role, key) in new[] { ("receiptAccrual", "receipt_accrual"), ("supplierPayable", "supplier_payable"), ("supplierAdvance", "supplier_advance") }) {
            var mapping = Assert.Single(chart["mappings"].AsBsonArray, row => row["mappingKey"] == key);
            Policy[role + "Mapping"] = key; Policy[role + "AccountId"] = mapping["accountId"]; Policy[role + "AccountCode"] = mapping["accountCode"];
        }
        await Rows(B.Policies).InsertOneAsync(Policy);
    }

    private async Task CreateVerifiedAdvanceAsync()
    {
        Funding = await ProcurementFundingTestData.AttachAsync(Receiving.Orders, Receiving.Proposal, Receiving.Order);
        var fundingRequest = await Funding.RequestAsync("payable-source-funding");
        var fundingDecision = await Funding.DecideAsync(fundingRequest, "payable-source-funding-approved");
        Beneficiaries = await SupplierBankTestData.PrepareAsync(Receiving.Orders.Suppliers, Receiving.Orders.Supplier);
        var beneficiaryProposal = await Beneficiaries.ProposeAsync("payable-bank-proposal");
        var beneficiaryObservation = await Beneficiaries.ObserveAsync(beneficiaryProposal, "payable-bank-observation");
        var beneficiary = await Beneficiaries.DecideAsync(beneficiaryProposal, "payable-bank-approved", beneficiaryObservation["recordId"].AsInt64);
        var chartBefore = await Rows("accountingChartVersions").Find(new BsonDocument("_id", Valuation.Book["chartId"])).SingleAsync();
        var creditDate = AccountingCalendar.BusinessDate(Clock.UtcNow);
        var creditOriginal = Encoding.UTF8.GetBytes(string.Join(',', BankStatementParser.Fields) + "\r\n0001," +
            creditDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",C,25000.00,Synthetic buyer,SHARED-REF,0000123456789,IDR\r\n");
        var creditPeriod = await Rows("periods").Find(new BsonDocument { { "bookId", Valuation.Book["bookId"] },
            { "month", creditDate.ToString("yyyy-MM", CultureInfo.InvariantCulture) } }).SingleAsync();
        Assert.Equal("open", creditPeriod["state"]);
        Bank = await BankReceiptTestData.AttachAsync(await BankStatementTestData.AttachAsync(await ReceivingAccountTestData.AttachAsync(Catalog)),
            original: creditOriginal, preserveApprovedAccounting: true);
        await Bank.ConfirmAsync(await Bank.PreviewAsync(), "payable-actual-cash-anchor",
            BankReceiptTestData.Observation(amount: 25000) with { BookedDate = creditDate });
        Assert.Equal(chartBefore, await Rows("accountingChartVersions").Find(new BsonDocument("_id", Valuation.Book["chartId"])).SingleAsync());
        string[] sourcePermissions = [Permissions.FundingRead, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.BankReceiptRead,
            Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        Recorder = await Catalog.ActorAsync(754, CatalogAuthoringTestData.Shop, [Permissions.ProcurementDisbursementRead,
            Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementRecord, .. sourcePermissions]);
        BankVerifier = await Catalog.ActorAsync(755, CatalogAuthoringTestData.Shop, [Permissions.ProcurementSettlementRead,
            Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementVerify, .. sourcePermissions]);
        await PersonTestData.SeedDistinctPeopleAsync(Database, Clock, Recorder.UserId, BankVerifier.UserId);
        var recordingPolicyId = (await new IdAllocator(Database).NextAsync(ProcurementDisbursementTestData.Policies)).Value;
        await Rows(ProcurementDisbursementTestData.Policies).InsertOneAsync(new BsonDocument {
            { "_id", recordingPolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
            { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) },
            { "payingAccountId", Bank.Statements.Selection.AccountId }, { "payingAccountVersionId", Bank.Statements.Selection.AccountVersionId },
            { "allowPartialRecordings", true }, { "reserveUnverified", true }, { "payoutRecordingProcedureReference", "SYNTHETIC-PRIVATE-ORIGINAL-PROCEDURE" },
            { "beneficiaryVerificationProcedureReference", "SYNTHETIC-INDEPENDENT-BENEFICIARY-PROCEDURE" }, { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER" },
            { "approvalReference", "SYNTHETIC-DISBURSEMENT-APPROVAL" }, { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime },
            { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime }, { "active", true }
        });
        var input = new ProcurementDisbursementInput(ProcurementRequestTestData.Location, fundingRequest["requestId"].AsInt64,
            fundingDecision["decisionId"].AsInt64, fundingDecision["authorizationId"].AsInt64, Receiving.Orders.Supplier["supplierId"].AsInt64,
            beneficiary["versionId"].AsInt64, beneficiary["version"].AsInt64, Bank.Statements.Selection.AccountId, Bank.Statements.Selection.AccountVersionId,
            recordingPolicyId, 1, new Money(6000), AccountingCalendar.BusinessDate(Clock.UtcNow), "SYNTHETIC-TRANSFER-0001");
        var preview = await Disbursements.PreviewAsync(Recorder, input);
        var draft = await Disbursements.CreateDraftAsync(Recorder, input, preview["binding"].AsString, Meta("payable-source-draft"));
        var draftId = draft["draftId"].AsInt64;
        var evidence = new ProcurementDisbursementEvidenceService(Database, Unit, new(Database), Clock, Disbursements, Documents.Registration);
        var reservation = await evidence.ReserveAsync(Recorder, draftId, 1, Meta("payable-source-reserve"));
        using var bytes = new MemoryStream(ImageDecoderTests.Png(13, 7));
        var upload = await evidence.UploadAsync(Recorder, draftId, reservation.ReservationId, reservation.Revision, bytes, Meta("payable-source-upload"));
        Assert.Equal("quarantined", upload.State); Assert.NotNull(upload.Evidence);
        Assert.Equal("completed", await Documents.Runtime.RunOnceAsync());
        preview = await Disbursements.PreviewRecordAsync(Recorder, draftId, 1, reservation.ReservationId, upload.Evidence.Reference);
        var metadata = Meta("payable-source-record");
        Recorded = await RecordWithDiagnosticsAsync(evidence, draftId, reservation.ReservationId, upload.Evidence.Reference,
            preview["binding"].AsString, metadata);
        var original = Encoding.UTF8.GetBytes(string.Join(',', BankStatementParser.Fields) + "\r\nPAYABLE-ADV-DEBIT-0001," +
            AccountingCalendar.BusinessDate(Clock.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            ",D,6000.00,Synthetic supplier,SYNTHETIC-TRANSFER-0001,0000123456789,IDR\r\n");
        var import = await Bank.Statements.StageAsync("payable-actual-debit", original, "synthetic-payable-debit.csv");
        var settlementPolicyId = (await new IdAllocator(Database).NextAsync(ProcurementSettlementTestData.Policies)).Value;
        var advance = Assert.Single(chartBefore["mappings"].AsBsonArray, row => row["mappingKey"] == "supplier_advance");
        var cash = Assert.Single(chartBefore["mappings"].AsBsonArray, row => row["mappingKey"] == "bank_cash");
        await Rows(ProcurementSettlementTestData.Policies).InsertOneAsync(new BsonDocument {
            { "_id", settlementPolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
            { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) }, { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime },
            { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime }, { "rule", "independent_exact_bank_debit" }, { "allowReferenceOnly", true },
            { "payingAccountId", Bank.Statements.Selection.AccountId }, { "payingAccountVersionId", Bank.Statements.Selection.AccountVersionId },
            { "currency", "IDR" }, { "bookId", Valuation.Book["bookId"] }, { "chartVersionId", Valuation.Book["chartId"] },
            { "supplierAdvanceMapping", "supplier_advance" }, { "supplierAdvanceAccountId", advance["accountId"] }, { "supplierAdvanceAccountCode", advance["accountCode"] },
            { "cashMapping", "bank_cash" }, { "cashAccountId", cash["accountId"] }, { "cashAccountCode", cash["accountCode"] },
            { "bankDebitVerificationProcedureReference", "SYNTHETIC-INDEPENDENT-DEBIT-PROCEDURE" }, { "beneficiaryVerificationProcedureReference", "SYNTHETIC-BENEFICIARY-PROCEDURE" },
            { "trustedBankChannelReference", "SYNTHETIC-INDEPENDENT-BANK-PORTAL" }, { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER" },
            { "approvalReference", "SYNTHETIC-FINANCE-ADVANCE-MAPPING-APPROVAL" }
        });
        var settlementInput = new ProcurementSettlementInput(settlementPolicyId, 1, import["importId"].AsInt64, 2, SupplierBankTestData.Details,
            "SYNTHETIC-INDEPENDENT-BENEFICIARY-OBSERVATION", "SYNTHETIC-INDEPENDENT-DEBIT-OBSERVATION");
        preview = await Settlements.PreviewAsync(BankVerifier, Recorded["disbursementId"].AsInt64, 1, settlementInput);
        Settlement = await Settlements.VerifyAsync(BankVerifier, Recorded["disbursementId"].AsInt64, 1, settlementInput,
            preview["binding"].AsString, true, Meta("payable-source-settlement"));
        var settled = await Rows(ProcurementSettlementTestData.Events).Find(new BsonDocument("_id", SettlementId)).SingleAsync();
        Assert.Equal("6000.00", settled["amount"].AsString);
        var recorded = await Rows(ProcurementDisbursementTestData.Records).Find(new BsonDocument("_id", Recorded["disbursementId"])).SingleAsync();
        Assert.Equal(Receiving.OrderId, recorded["source"]["funding"]["request"]["orderSource"]["order"]["_id"].AsInt64);
    }

    internal async Task<BsonDocument> ProposeAsync(string key, ProcurementSupplierPayableInput? input = null)
    {
        input ??= Recognize(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }

    // Additional genuine payout against the same finite authorization/account/book.
    // Used by supplier-return tests; default M75 setup and its immutable first payout stay unchanged.
    internal async Task<BsonDocument> CreateAdditionalVerifiedAdvanceAsync(string key, decimal amount = 4000,
        ProcurementDisbursementInput? sourceInput = null, ReceivingAccountDetails? observedBeneficiary = null)
    {
        var bank = Bank ?? throw new InvalidOperationException("Verified advance setup is required");
        var firstDraft = await Rows(ProcurementDisbursementBson.Drafts).Find(new BsonDocument("_id", Recorded["draftId"])).SingleAsync();
        var input = (sourceInput ?? ProcurementDisbursementBson.Input(firstDraft["input"])) with {
            Amount = new Money(amount), TransferReference = "SYNTHETIC-TRANSFER-" + key,
            TransferDate = AccountingCalendar.BusinessDate(Clock.UtcNow)
        };
        var preview = await TimedAsync(key + ":draft-preview", () => Disbursements.PreviewAsync(Recorder, input));
        var draft = await TimedAsync(key + ":draft", () => Disbursements.CreateDraftAsync(Recorder, input, preview["binding"].AsString, Meta(key + "-draft")));
        var draftId = draft["draftId"].AsInt64;
        var evidence = new ProcurementDisbursementEvidenceService(Database, Unit, new(Database), Clock, Disbursements, Documents.Registration);
        var reservation = await TimedAsync(key + ":reserve", () => evidence.ReserveAsync(Recorder, draftId, 1, Meta(key + "-reserve")));
        using var bytes = new MemoryStream(ImageDecoderTests.Png(13, 7));
        var upload = await TimedAsync(key + ":upload", () => evidence.UploadAsync(Recorder, draftId, reservation.ReservationId, 1, bytes, Meta(key + "-upload")));
        Assert.NotNull(upload.Evidence); Assert.Equal("quarantined", upload.State);
        Assert.Equal("completed", await TimedAsync(key + ":validate", () => Documents.Runtime.RunOnceAsync()));
        preview = await TimedAsync(key + ":record-preview", () => Disbursements.PreviewRecordAsync(Recorder, draftId, 1, reservation.ReservationId, upload.Evidence.Reference));
        var recorded = await RecordWithDiagnosticsAsync(evidence, draftId, reservation.ReservationId, upload.Evidence.Reference,
            preview["binding"].AsString, Meta(key + "-record"));
        var original = Encoding.UTF8.GetBytes(string.Join(',', BankStatementParser.Fields) + "\r\nDEBIT-" + key + "," +
            input.TransferDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",D," + input.Amount +
            ",Synthetic supplier," + input.TransferReference + ",0000123456789,IDR\r\n");
        var import = await bank.Statements.StageAsync(key + "-bank-import", original, "synthetic-" + key + ".csv");
        var first = await Rows(ProcurementSettlementTestData.Events).Find(new BsonDocument("_id", SettlementId)).SingleAsync();
        var settlementInput = new ProcurementSettlementInput(first["input"]["policyId"].AsInt64, first["input"]["policyRevision"].AsInt64,
            import["importId"].AsInt64, 2, observedBeneficiary ?? SupplierBankTestData.Details, "SYNTHETIC-INDEPENDENT-BENEFICIARY-" + key, "SYNTHETIC-INDEPENDENT-DEBIT-" + key);
        preview = await Settlements.PreviewAsync(BankVerifier, recorded["disbursementId"].AsInt64, 1, settlementInput);
        return await Settlements.VerifyAsync(BankVerifier, recorded["disbursementId"].AsInt64, 1, settlementInput,
            preview["binding"].AsString, true, Meta(key + "-settlement"));
    }
    private async Task<T> TimedAsync<T>(string stage, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try {
            var result = await action();
            diagnostics?.Invoke("PAYABLE_SOURCE_STAGE " + JsonSerializer.Serialize(new { stage, outcome = "completed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }));
            return result;
        } catch (Exception error) {
            diagnostics?.Invoke("PAYABLE_SOURCE_STAGE " + JsonSerializer.Serialize(new { stage, outcome = "failed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, errorType = error.GetType().Name }));
            throw;
        }
    }
    private async Task<BsonDocument> RecordWithDiagnosticsAsync(ProcurementDisbursementEvidenceService facade, long draftId,
        long reservationId, EvidenceRef reference, string binding, AuthorizationCommandMetadata metadata)
    {
        if (diagnostics is null) return await facade.RecordAsync(Recorder, draftId, 1, reservationId, reference, binding, metadata);
        var recordingOwner = Disbursements;
        var originalReader = new EvidenceService(Database, Unit, new(Database), Clock, Documents.Registration, procurementDisbursementTargets: recordingOwner);
        // Same recovery, storage admission and bound-owner composition as the actual
        // facade. Diagnostics observe existing stages; all proofs and budgets remain.
        AuthorizationStore.ValidateMetadata(metadata);
        var hash = AuthorizationStore.CommandHash(ProcurementDisbursementBson.RecordBody(draftId, 1, reservationId, reference, binding), metadata.Reason);
        var recovered = await TimedAsync(metadata.IdempotencyKey + ":body-lookup", () => recordingOwner.RecoverRecordBodyAsync(Recorder, draftId, metadata.IdempotencyKey, hash, default));
        if (recovered is not null) return recovered;
        await TimedAsync(metadata.IdempotencyKey + ":storage", async () => {
            await recordingOwner.StorageAsync(default); await ProcurementDisbursementEvidenceSchemas.RequireStorageAsync(Database, default); return true;
        });
        return await TimedAsync(metadata.IdempotencyKey + ":protected-record", () => originalReader.WithProcurementDisbursementPinAsync(Recorder,
            new(EvidenceTargetKind.ProcurementDisbursement, draftId), 1, reservationId, reference,
            new("procurement-disbursement-evidence-view", metadata.CorrelationId), async (claim, handle, started, ct) => {
                Assert.Equal(TimeSpan.FromSeconds(5), claim.InitialLeaseDuration); Assert.Equal(TimeSpan.FromSeconds(60), claim.RequestDuration);
                void Observe(string stage, double? requestElapsed = null, bool? readable = null) => diagnostics("PAYABLE_SOURCE_PIN_STAGE " + JsonSerializer.Serialize(new {
                    operationKey = metadata.IdempotencyKey, stage, observedAt = DateTimeOffset.UtcNow,
                    requestElapsedMs = requestElapsed ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    leaseElapsedMs = Stopwatch.GetElapsedTime(claim.LeaseStarted).TotalMilliseconds,
                    canRead = readable ?? handle.Stream.CanRead,
                    initialLeaseMs = claim.InitialLeaseDuration.TotalMilliseconds, requestBudgetMs = claim.RequestDuration.TotalMilliseconds
                }));
                Observe("original-verified");
                try {
                    var result = await recordingOwner.RecordWithProofAsync(Recorder, draftId, 1, reservationId, reference, binding, metadata,
                        async (tx, id, target, inner) => {
                            Observe("pin-enter");
                            var pin = new EvidenceService(Database, Unit, new(Database), Clock, Documents.Registration, procurementDisbursementTargets: target) {
                                ProcurementDisbursementPinObserver = (elapsed, canRead) => Observe("pin-final", elapsed, canRead)
                            };
                            var final = await pin.PinProcurementDisbursementAsync(tx, Recorder, id, claim, handle, started, inner);
                            Observe("pin-created"); return final;
                        }, ct);
                    Observe("record-completed"); return result;
                } catch { Observe("record-failed"); throw; }
            }));
    }
    internal async Task<BsonDocument> DecideAsync(long proposalId, string key, ProcurementSupplierPayableDecision decision = ProcurementSupplierPayableDecision.Approve)
    {
        var preview = await Service.PreviewDecisionAsync(Checker, proposalId, decision);
        return await Service.DecideAsync(Checker, proposalId, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> ProtectedAsync()
    {
        var result = new BsonDocument { { "originals", await Valuation.PreservedAsync() } };
        foreach (var table in new[] { "procurementStockValuationAllocations", "procurementFundingAllocations", "procurementDisbursementRecords",
            "procurementDisbursementAllocations", "procurementSettlements", "bankTransactionClaims", "bankReceipts", "cashMovements", "paymentAllocations", "reservations" })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task<BsonDocument> StateAsync()
    {
        var state = new BsonDocument();
        foreach (var table in B.Collections.Concat(["journalEntries", "accountingAuditEvents", "accountingPeriodHistory", "periods", "accountingBooks"])) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.payable\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync() => Valuation.DisposeAsync();
}

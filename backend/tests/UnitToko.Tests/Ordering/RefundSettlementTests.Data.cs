using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Tests.Ordering;

[SupportedOSPlatform("windows")]
public sealed partial class RefundSettlementTests
{
    internal sealed class Data(RefundPayoutTests.Data payout) : IAsyncDisposable
    {
        internal const long VerifierId = 7691;
        internal RefundPayoutTests.Data Payout { get; } = payout;
        internal IMongoDatabase Database => Payout.Database;
        internal CatalogAuthoringTestData Catalog => Payout.Catalog;
        internal UnitToko.Platform.IClock Clock => Payout.Clock;
        internal RefundPayoutService Service => Payout.Service;
        internal PaymentAllocationService AllocationService => Payout.Source.Allocation.Service;
        internal BankReceiptTestData Bank => Payout.Source.Allocation.Source.Bank;
        internal ActorContext Verifier = null!;
        internal ActorContext Importer => Bank.Statements.Maker;
        internal long PayoutId;
        internal BsonDocument PayoutRecord = null!;
        internal RefundSettlementInput Input = null!;
        internal Task<RefundSettlementPreview> PreviewAsync(ActorContext? actor = null, RefundSettlementInput? input = null) =>
            Service.PreviewSettlementAsync(actor ?? Verifier, PayoutId, input ?? Input);
        internal Task<BsonDocument> VerifyAsync(RefundSettlementPreview preview, string key, ActorContext? actor = null, RefundSettlementInput? input = null) =>
            Service.VerifySettlementAsync(actor ?? Verifier, PayoutId, input ?? Input, preview.Binding, true, Meta(key));
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key,
            "Synthetic independent confirmation of exact recorded refund against real fixture bank debit; no transfer executed", "synthetic-refund-settlement");
        internal static async Task<Data> CreateAsync(MongoFixture fixture) {
            var payout = await RefundPayoutTests.Data.CreateAsync(fixture);
            var observations = new List<string>(); var originalObserver = payout.PhaseObserver;
            payout.PhaseObserver = (phase, elapsedMs) => {
                observations.Add(JsonSerializer.Serialize(new { stage = "source/" + phase, elapsedMs,
                    canRead = phase == "pin-check-open" ? true : phase == "pin-check-closed" ? false : (bool?)null }));
                originalObserver?.Invoke(phase, elapsedMs);
            };
            var stage = "upload"; var started = Stopwatch.GetTimestamp();
            void Next(string next) {
                observations.Add(JsonSerializer.Serialize(new { stage, status = "completed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }));
                stage = next; started = Stopwatch.GetTimestamp();
            }
            try {
                var data = new Data(payout);
                await payout.UploadAsync(); Next("validate"); await payout.ValidateAsync(); Next("preview-record");
                var preview = await payout.PreviewRecordAsync(); Next("record");
                var recorded = await payout.RecordAsync(preview, RefundPayoutTests.Data.Meta("settlement-fixture-payout")); Next("settlement-source-setup");
                data.PayoutId = recorded["payoutId"].AsInt64;
                data.PayoutRecord = await data.Rows(RefundPayoutBson.Payouts).Find(new BsonDocument("_id", data.PayoutId)).SingleAsync();
                data.Verifier = await data.Catalog.ActorAsync(VerifierId, CatalogAuthoringTestData.Shop, [Permissions.CaseRead, Permissions.InvoiceRead,
                    Permissions.PaymentReviewRead, Permissions.DisbursementVerify, Permissions.BankReceiptRead, Permissions.BankStatementRead,
                    Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead]);
                await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Verifier.UserId);
                Assert.NotEqual(payout.Actor.UserId, data.Importer.UserId); Assert.NotEqual(payout.Actor.UserId, data.Verifier.UserId);
                Assert.NotEqual(data.Importer.UserId, data.Verifier.UserId);
                var imported = await data.ImportDebitAsync("SETTLEMENT-0001", "settlement-fixture-debit");
                data.Input = new(imported, 2, "synthetic-independent-beneficiary-bank-record", "synthetic-independent-transfer-debit-record");
                var policy = new BsonDocument { { "_id", (await new IdAllocator(data.Database).NextAsync(D.Policies)).Value }, { "schemaVersion", 1 },
                    { "revision", 1L }, { "scope", data.PayoutRecord["source"]["payingBank"]["scope"].DeepClone() }, { "rule", "independent_exact_bank_debit" },
                    { "active", true }, { "approvedAt", data.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime }, { "fence", 0L } };
                foreach (var field in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
                    policy[field] = "synthetic-test-only-" + field;
                await data.Rows(D.Policies).InsertOneAsync(policy);
                return data;
            } catch (Exception error) {
                observations.Add(JsonSerializer.Serialize(new { stage, status = "failed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, errorType = error.GetType().Name }));
                // Failure-only diagnostics retain no actors, keys, bank details or original bytes.
                try {
                    var directory = new DirectoryInfo(AppContext.BaseDirectory);
                    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) directory = directory.Parent;
                    if (directory is null) throw new InvalidOperationException("The authoritative checkout was not found");
                    var folder = Path.Combine(directory.FullName, "tmp", "production", "ocr-results"); Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, "refund-settlement-setup-failure-" + Guid.NewGuid().ToString("N") + ".jsonl");
                    await File.WriteAllLinesAsync(path, observations);
                    foreach (var observation in observations) Console.WriteLine("REFUND_SETTLEMENT_SETUP " + observation);
                    Console.WriteLine("REFUND_SETTLEMENT_SETUP_TRACE " + path);
                } catch (Exception traceError) { Console.WriteLine("REFUND_SETTLEMENT_SETUP_TRACE_FAILED " + traceError.GetType().Name); }
                await payout.DisposeAsync(); throw;
            } finally { payout.PhaseObserver = originalObserver; }
        }
        internal async Task<long> ImportDebitAsync(string transactionId, string key, decimal amount = 600m, bool credit = false) {
            var bytes = BankStatementTestData.Original(transactionId, amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            var csv = Encoding.UTF8.GetString(bytes).Replace("2026-09-27", Payout.Input.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!credit) csv = csv.Replace(",C,", ",D,", StringComparison.Ordinal);
            return (await Bank.Statements.StageAsync(key, Encoding.UTF8.GetBytes(csv), "synthetic-actual-settlement.csv"))["importId"].AsInt64;
        }
        // Keep original finance/stock/source hashes. Settlement may change only its
        // receipt chain head and introduce its one outgoing accounting event.
        internal async Task<BsonDocument> PermanentAsync() {
            var result = await Payout.PreservedAsync(); result.Remove(PaymentAllocationBson.Receipts);
            result["journalEntries"] = new BsonArray(await Rows("journalEntries").Find(new BsonDocument("source.namespace", new BsonDocument("$ne", D.Operation)))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
            foreach (var name in new[] { RefundPayoutBson.Payouts, "evidencePins", "evidencePinAudits", "bankTransactionClaims" }) {
                var filter = name == "bankTransactionClaims" ? new BsonDocument("direction", "credit") : new BsonDocument();
                var rows = await Rows(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
            }
            return result;
        }
        internal async Task<BsonDocument> SettlementStateAsync() {
            var result = new BsonDocument();
            foreach (var name in new[] { D.Events, PaymentAllocationBson.Receipts, PaymentAllocationBson.Invoices, "journalEntries", "bankTransactionClaims",
                "accountingBooks", "periods", "accountingAuditEvents", "accountingPeriodHistory" }) {
                var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
            }
            foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                result[name] = new BsonArray(await Rows(name).Find(name == "outboxEvents" ? new BsonDocument() : new BsonDocument(field, D.Operation))
                    .Sort(new BsonDocument("_id", 1)).ToListAsync());
            return result;
        }
        public ValueTask DisposeAsync() => Payout.DisposeAsync();
    }
}

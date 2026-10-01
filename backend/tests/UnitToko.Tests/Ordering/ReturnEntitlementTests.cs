using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnEntitlementTests(MongoFixture fixture)
{
    internal static async Task SeedPolicyAsync(InvoiceData data) {
        var clock = data.Order.Prices.Catalog.Clock; var shop = CatalogAuthoringTestData.Shop;
        var row = new BsonDocument { { "_id", Data.PolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
            { "scope", new BsonDocument { { "fulfillmentOrganizationId", shop.OrganizationId!.Value }, { "shopId", shop.ShopId!.Value }, { "channelId", shop.ChannelId!.Value }, { "warehouseId", BsonNull.Value } } },
            { "netRule", "tail_backout" }, { "componentRule", "manual_component_allocation" }, { "requiredIdentity", "matched" }, { "active", true },
            { "approvedAt", clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime } };
        foreach (var field in new[] { "attributionProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "creditNoteProcedureReference", "operatingOwnerReference", "approvalReference" }) row[field] = "synthetic-approved-" + field;
        await data.Rows(E.Policies).InsertOneAsync(row);
    }
    [WindowsStorageTheory]
    [InlineData("credit")] [InlineData("refund")]
    public async Task Exact_inspected_financial_request_recovers_original_and_reserves_line_attribution_without_approving_or_moving_money(string action) {
        await using var data = await Data.CreateAsync(fixture, action, paid: action == "credit");
        var before = await data.BusinessAsync(); var initial = await data.Service.ReadAsync(data.Actor, data.CaseId, data.ReturnId, action);
        Assert.Null(initial.Request); Assert.Equal(0L, initial.Pool["version"].AsInt64);
        var preview = await data.PreviewAsync(); Assert.Equal(1000.02m, preview.Result.Net.Value); Assert.Equal(0m, preview.Result.Tax.Value); Assert.Equal(0m, preview.Result.Charges.Value);
        Assert.Equal(1L, Assert.Single(preview.Result.Lines).AttributedBaseUnits);
        var metadata = Data.Meta("entitlement-original"); var saved = await data.RequestAsync(preview, metadata);
        Assert.Equal("financial_review_requested", saved["state"].AsString); Assert.Equal(E.Operation, saved["operation"].AsString); Assert.Equal(action, saved["action"].AsString);
        Assert.Equal(saved, await data.RequestAsync(preview, metadata)); Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, action, metadata.IdempotencyKey));
        Assert.Null(await data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, action, "other-unknown-key"));
        var current = await data.Service.ReadAsync(data.Actor, data.CaseId, data.ReturnId, action); Assert.Equal(saved, current.Request);
        Assert.Equal(1L, current.Pool["version"].AsInt64); Assert.Equal(1L, current.Pool["lines"][0]["attributedBaseUnits"].AsInt64); Assert.Equal(1000.02m, current.Pool["lines"][0]["net"].AsDecimal);
        var stored = await data.Rows(E.Requests).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(6000.12m, stored["source"]["issuedLines"][0]["net"].AsDecimal); Assert.Equal(12000.25m, stored["source"]["issuedLines"][0]["packagePrice"].AsDecimal);
        Assert.Equal(1L, stored["source"]["inspectionLines"][0]["inspectedBaseUnits"].AsInt64); Assert.Equal("matched", stored["source"]["inspectionLines"][0]["identity"].AsString);
        foreach (var (table, field) in new[] { (E.Requests, "action"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, table == E.Requests ? action : E.Operation)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RequestAsync(preview, metadata with { Reason = "Changed reviewed request body" }));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RequestAsync(preview, Data.Meta("cannot-overwrite-entitlement")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync());
        await data.AssertBusinessAsync(before);
        if (action == "credit") {
            var paid = await data.Rows(B.Invoices).Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync(); Assert.Equal(1000m, paid["paid"].AsDecimal);
            Assert.Equal(0m, paid["credited"].AsDecimal); var funds = await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync(); Assert.Equal(0m, funds["reservedForRefund"].AsDecimal);
        }
    }

    [Fact]
    public async Task Missing_current_grant_fresh_MFA_source_scope_person_and_approved_attribution_policy_cannot_create_or_recover_financial_requests() {
        await using var data = await Data.CreateAsync(fixture, policy: false);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync()); await data.PolicyAsync(); var preview = await data.PreviewAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.CaseId, data.ReturnId, data.Action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Payment.Invoice.Order.Buyer, data.CaseId, data.ReturnId, data.Action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Payment.Invoice.Order.Prices.Catalog.Other, data.CaseId, data.ReturnId, data.Action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Actor, data.CaseId, data.ReturnId, "refund"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Data.FinancialGrantId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.CreditRequest));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RequestAsync(preview, Data.Meta("protected-entitlement")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, "protected-entitlement"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Data.FinancialGrantId), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.CreditRequest));
        await data.Rows(E.Policies).UpdateOneAsync(new BsonDocument("_id", Data.PolicyId), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RequestAsync(preview, Data.Meta("protected-entitlement")));
        Assert.Equal(0, await data.Rows(E.Requests).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(E.Pools).CountDocumentsAsync(new BsonDocument()));
        await data.Rows(E.Policies).UpdateOneAsync(new BsonDocument("_id", Data.PolicyId), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddDays(1).UtcDateTime));
        var saved = await data.RequestAsync(preview, Data.Meta("protected-entitlement"));
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Payment.Invoice.Order.Buyer.UserId), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, "protected-entitlement"));
        Assert.Equal(saved, E.Result(await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync()));
    }

    [Fact]
    public async Task Concurrent_financial_requests_have_one_winner_and_the_original_attribution_cannot_be_replaced() {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => data.RequestAsync(preview, Data.Meta("entitlement-race-a"))), Record.ExceptionAsync(() => data.RequestAsync(preview, Data.Meta("entitlement-race-b"))));
        Assert.Single(outcomes, error => error is null); Assert.Single(outcomes, error => error is not null);
        var row = await data.Rows(E.Requests).Find(new BsonDocument()).SingleAsync(); var pool = await data.Rows(E.Pools).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, pool["version"].AsInt64); Assert.Equal(1L, pool["lines"][0]["attributedBaseUnits"].AsInt64); Assert.Equal(1000.02m, pool["lines"][0]["net"].AsDecimal);
        Assert.Equal(E.Result(row), await data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, row["requestKey"].AsString));
        Assert.Null(await data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, row["requestKey"] == "entitlement-race-a" ? "entitlement-race-b" : "entitlement-race-a"));
    }

    [Fact]
    public async Task Coherent_financial_history_dated_before_its_actual_resolution_is_rejected_by_the_owning_source_on_every_path() {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); var metadata = Data.Meta("entitlement-before-source");
        await data.RequestAsync(preview, metadata); var before = await data.BusinessAsync();
        var proposal = await data.Rows(ReturnResolutionRecords.Collection).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        var row = await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync();
        var earlier = new BsonDateTime(proposal["requestedAt"].ToUniversalTime().AddMilliseconds(-1));
        Assert.True(earlier.CompareTo(row["policy"]["approvedAt"]) >= 0); Assert.True(earlier.CompareTo(proposal["requestedAt"]) < 0);
        row["requestedAt"] = earlier; row["snapshotHash"] = B.Hash(row); var result = E.Result(row);
        Assert.Equal(1, (await data.Rows(E.Requests).ReplaceOneAsync(new BsonDocument("_id", data.ReturnId), row)).ModifiedCount);
        Assert.Equal(1, (await data.Rows("auditEvents").ReplaceOneAsync(new BsonDocument("_id", row["auditId"]), E.Audit(row))).ModifiedCount);
        Assert.Equal(1, (await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("sourceKey", E.Operation + ":" + data.ReturnId),
            Builders<BsonDocument>.Update.Set("createdAt", earlier).Set("dueAt", earlier).Set("payload", result))).ModifiedCount);
        Assert.Equal(1, (await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument { { "actorId", data.Actor.UserId }, { "operation", E.Operation }, { "key", metadata.IdempotencyKey } },
            Builders<BsonDocument>.Update.Set("createdAt", earlier).Set("result", result))).ModifiedCount);
        var head = await data.Rows(E.Pools).Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync();
        head["lastRequestHash"] = row["snapshotHash"]; head["snapshotHash"] = B.Hash(head);
        Assert.Equal(1, (await data.Rows(E.Pools).ReplaceOneAsync(new BsonDocument("_id", head["_id"]), head)).ModifiedCount);
        // Schema, money, hashes and effects are coherent. Only the actual immutable producer's
        // timestamp proves that this financial event cannot have occurred yet.
        var raw = await new MongoUnitOfWork(data.Payment.Invoice.Order.Prices.Catalog.Client).ExecuteAsync((tx, ct) => new ReturnEntitlementRecords(data.Database, data.Clock).RawAsync(tx, data.ReturnId, ct));
        Assert.Equal(row, raw);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Actor, data.CaseId, data.ReturnId, data.Action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RequestAsync(preview, metadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RequestAsync(preview, Data.Meta("cannot-rewrite-before-source")));
        Assert.Equal(row, await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync());
        Assert.Equal(head, await data.Rows(E.Pools).Find(new BsonDocument("_id", head["_id"])).SingleAsync()); await data.AssertBusinessAsync(before);
    }

    [Theory]
    [InlineData("request")] [InlineData("pool")] [InlineData("outbox")] [InlineData("proposal")]
    public async Task Missing_durable_request_pool_effect_or_original_resolution_never_means_zero_attribution_and_cannot_recreate_history(string fault) {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); var metadata = Data.Meta("entitlement-history"); await data.RequestAsync(preview, metadata);
        var (table, filter) = fault switch {
            "request" => (E.Requests, new BsonDocument("_id", data.ReturnId)), "pool" => (E.Pools, new BsonDocument("_id", data.Payment.Issued.Id.Value)),
            "proposal" => (ReturnResolutionRecords.Collection, new BsonDocument("_id", data.ReturnId)), _ => ("outboxEvents", new BsonDocument("sourceKey", E.Operation + ":" + data.ReturnId)) };
        Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Actor, data.CaseId, data.ReturnId, data.Action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RequestAsync(preview, Data.Meta("cannot-recreate-entitlement")));
    }

    [Theory]
    [InlineData("auditEvents")] [InlineData("outboxEvents")]
    public async Task Required_effect_failure_rolls_back_request_and_line_pool_and_the_same_original_key_can_retry(string table) {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); var before = await data.BusinessAsync();
        var collection = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
        var originalValidator = collection["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).DeepClone().AsBsonDocument;
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { originalValidator, new BsonDocument("kind", new BsonDocument("$ne", E.Operation)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var metadata = Data.Meta("entitlement-rollback"); Assert.NotNull(await Record.ExceptionAsync(() => data.RequestAsync(preview, metadata)));
        Assert.Equal(0, await data.Rows(E.Requests).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(E.Pools).CountDocumentsAsync(new BsonDocument()));
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument(field, E.Operation)));
        await data.AssertBusinessAsync(before);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", originalValidator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var saved = await data.RequestAsync(preview, metadata); Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, data.CaseId, data.ReturnId, data.Action, metadata.IdempotencyKey));
        Assert.Equal(1, await data.Rows(E.Pools).CountDocumentsAsync(new BsonDocument())); await data.AssertBusinessAsync(before);
    }

    internal sealed class Data(PaymentDraftData payment, IAsyncDisposable owner, AfterSalesCaseService cases, long caseId, long returnId, string action) : IAsyncDisposable
    {
        internal const long FinancialGrantId = 99870, PolicyId = 99871;
        internal PaymentDraftData Payment { get; } = payment;
        internal IMongoDatabase Database => Payment.Invoice.Order.Database;
        internal UnitToko.Platform.IClock Clock => Payment.Invoice.Order.Prices.Catalog.Clock;
        internal ActorContext Actor => Payment.Invoice.Finance;
        internal long CaseId { get; } = caseId;
        internal long ReturnId { get; } = returnId;
        internal string Action { get; } = action;
        internal ReturnEntitlementService Service => new(Database, new(Payment.Invoice.Order.Prices.Catalog.Client), new(Database), Clock, cases, Payment.Invoice.Service);
        internal ReturnEntitlementInput Input { get; private set; } = null!;
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic reviewed immutable return attribution; no credit approval or payment", "synthetic-return-entitlement");
        internal Task<ReturnEntitlementPreview> PreviewAsync() => Service.PreviewAsync(Actor, CaseId, ReturnId, Action, Input);
        internal Task<BsonDocument> RequestAsync(ReturnEntitlementPreview preview, AuthorizationCommandMetadata metadata) => Service.RequestAsync(Actor, CaseId, ReturnId, Action, preview.View.Pool["version"].AsInt64, preview.Binding, Input, metadata);
        internal Task PolicyAsync() => SeedPolicyAsync(Payment.Invoice);
        internal static async Task<Data> CreateAsync(MongoFixture fixture, string action = "credit", bool policy = true, bool paid = false) {
            PaymentAllocationTests.Data? allocation = paid ? await PaymentAllocationTests.Data.CreateAsync(fixture) : null;
            var payment = allocation?.Source.Read.Payment ?? await PaymentDraftData.CreateAsync(fixture); IAsyncDisposable owner = allocation is null ? payment : allocation;
            try {
                if (allocation is not null) {
                    var input = allocation.Input(0, 1000m); var preview = await allocation.Service.PreviewAsync(allocation.Actor, allocation.VerificationId, input);
                    await allocation.Service.AllocateAsync(allocation.Actor, allocation.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("entitlement-original-paid-allocation"));
                }
                var (cases, caseId, returnId) = await ReturnResolutionTests.Setup(payment); var actor = payment.Invoice.Finance;
                await payment.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, FinancialGrantId, CatalogAuthoringTestData.Shop, [ReturnResolutionPolicy.Permission(action)]);
                var proposal = await cases.ReadResolutionAsync(actor, caseId, returnId, action);
                await cases.RequestResolutionAsync(actor, caseId, returnId, 0, proposal.Binding, new(action, "Permintaan penyelesaian hasil pemeriksaan"), CatalogPriceTestData.Meta("entitlement-source-resolution"));
                var data = new Data(payment, owner, cases, caseId, returnId, action);
                var line = await data.Rows("returnInspections").Find(new BsonDocument("_id", returnId)).SingleAsync(); var inspected = line["input"]["lines"][0];
                data.Input = new([new(inspected["receiptId"].AsInt64, inspected["skuId"].AsInt64, new Money(0m), new Money(0m))]);
                if (policy) await data.PolicyAsync(); return data;
            } catch { await owner.DisposeAsync(); throw; }
        }
        private static readonly string[] BusinessTables = ["stockBalances", "lots", "movements", "reservations", InvoiceBson.Invoices, InvoiceBson.Allocations,
            BankReceiptBson.Receipts, B.Events, PaymentAllocationCorrectionBson.Events, B.Receipts, B.Invoices, "journalEntries", "returnQuarantineReceipts", "returnInspections", ReturnResolutionRecords.Collection];
        private async Task<List<BsonDocument>> BusinessRowsAsync(string table) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); } return rows;
        }
        internal async Task<Dictionary<string, List<BsonDocument>>> BusinessAsync() {
            var result = new Dictionary<string, List<BsonDocument>>(); foreach (var table in BusinessTables) result[table] = await BusinessRowsAsync(table); return result;
        }
        internal async Task AssertBusinessAsync(Dictionary<string, List<BsonDocument>> before) { foreach (var table in BusinessTables) Assert.Equal(before[table], await BusinessRowsAsync(table)); }
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}

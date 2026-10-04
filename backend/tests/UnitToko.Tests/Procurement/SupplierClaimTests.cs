using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.SupplierClaimTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class SupplierClaimTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData(ProcurementRoute.OmiRegular)] [InlineData(ProcurementRoute.Ddo)] [InlineData(ProcurementRoute.Smart)] [InlineData(ProcurementRoute.PasarKering)]
    public async Task Genuine_physical_claim_and_independent_decision_preserve_quarantine_and_original_sources_on_every_route(ProcurementRoute route) {
        await using var data = await D.CreateAsync(fixture, route); var inspection = await data.Inspection.StateAsync(); var receiving = await data.Inspection.Receiving.StateAsync();
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var requested = await data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("actual-claim"));
        Assert.Equal("requested", requested["state"].AsString); var id = requested["claimId"].AsInt64;
        Assert.Equal(requested, await data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("actual-claim")));
        Assert.Equal(requested, await data.Service.LookupAsync(data.Maker, D.RequestOperation, "actual-claim"));
        var approved = await data.DecideAsync(id, "actual-independent-approval"); Assert.Equal("approved_pending_supplier_action", approved["state"].AsString);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.ApproveOperation, "actual-independent-approval"));
        Assert.Equal("approved_pending_supplier_action", (await data.Service.ReadAsync(data.Reader, id))["state"].AsString);
        await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Checker, id, SupplierClaimDecision.Reject));
        Assert.Equal(inspection, await data.Inspection.StateAsync()); Assert.Equal(receiving, await data.Inspection.Receiving.StateAsync());
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Decision_cannot_reuse_a_verified_request_after_its_stored_bytes_change_in_the_same_transaction() {
        await using var data = await D.CreateAsync(fixture);
        var requested = await data.RequestAsync("claim-decision-source-byte-proof");
        var claimId = requested["claimId"].AsInt64;
        await data.DecideAsync(claimId, "claim-decision-source-byte-proof-approved");
        var original = await data.Rows("supplierClaimRequests").Find(new BsonDocument("_id", claimId)).SingleAsync();
        var before = await data.StateAsync();
        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var records = new SupplierClaimRecords(data.Database, data.Unit, data.Clock,
                data.Inspection.Receiving.Procurement.Catalog, data.Inspection.Receiving.Orders.Service);
            var verified = await records.RequestAsync(tx, claimId, ct);
            var changed = verified.DeepClone().AsBsonDocument;
            changed["reason"] = "Synthetic changed request after verification";
            changed["input"]["reason"] = changed["reason"];
            Assert.Equal(1L, (await data.Rows("supplierClaimRequests").ReplaceOneAsync(tx,
                new BsonDocument("_id", claimId), changed, cancellationToken: ct)).ModifiedCount);
            _ = await records.DecisionAsync(tx, verified, ct);
            return true;
        }));
        Assert.Equal("supplier_claim_integrity_unavailable", failure.Code);
        Assert.Equal(original, await data.Rows("supplierClaimRequests").Find(new BsonDocument("_id", claimId)).SingleAsync());
        Assert.Equal(before, await data.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Shortage_is_observed_missing_quantity_and_rejection_never_reopens_physical_disposition() {
        await using var data = await D.CreateAsync(fixture, shortage: true);
        var shortage = data.Input("SHORTAGE-001", 2, SupplierClaimBasis.ShortageObserved); var missing = await data.RequestAsync("missing-observed", shortage);
        await data.DecideAsync(missing["claimId"].AsInt64, "missing-investigation-approved");
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, shortage with { ClaimReference = "SHORTAGE-002", Lines = [shortage.Lines[0] with { Packages = 1 }] }));
        var physical = data.Input("PHYSICAL-001", 3) with { Lines = [data.Input().Lines[0] with { Packages = 3, Issue = "other" }] };
        var request = await data.RequestAsync("quarantined-physical", physical); var rejected = await data.DecideAsync(request["claimId"].AsInt64, "rejected-no-release", SupplierClaimDecision.Reject);
        Assert.Equal("rejected_quarantined", rejected["state"].AsString);
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, physical with { ClaimReference = "PHYSICAL-002", Lines = [physical.Lines[0] with { Packages = 1 }] }));
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Invoice_financial_basis_is_exact_and_same_person_accounts_cannot_independently_approve() {
        await using var data = await D.CreateAsync(fixture, invoice: true); var input = data.Input(resolution: SupplierClaimResolution.Credit);
        var invoice = await data.Documents!.StateAsync(); var request = await data.RequestAsync("invoice-backed-claim", input); var id = request["claimId"].AsInt64;
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { ClaimReference = "FOREIGN-MATCH", Lines = [input.Lines[0] with { InvoiceMatchId = long.MaxValue - 2 }] }));
        await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Maker, id, SupplierClaimDecision.Approve));
        await OrganizationAddressTests.MergePeopleAsync(data.Inspection.Receiving.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Checker, id, SupplierClaimDecision.Approve));
        Assert.Equal(invoice, await data.Documents.StateAsync()); Assert.Null((await data.Service.ReadAsync(data.Reader, id))["decision"] as BsonDocument);
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Request_rollback_and_competing_capacity_claims_are_atomic_and_missing_retained_proofs_cannot_reopen_quantity() {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(packages: 2) with { Lines = [data.Input().Lines[0] with { Packages = 2, Issue = "other" }] };
        var preview = await data.Service.PreviewAsync(data.Maker, input); var before = await data.StateAsync();
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
        try {
            await GuardAsync(data, new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", D.RequestOperation)) }));
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("rolled-back"))));
            Assert.Equal(before, await data.StateAsync()); Assert.Null(await data.Service.LookupAsync(data.Maker, D.RequestOperation, "rolled-back"));
        } finally { await GuardAsync(data, validator); }
        var second = input with { ClaimReference = "SYNTHETIC-CLAIM-002" }; var other = await data.Service.PreviewAsync(data.Maker, second);
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("race-one"))),
            Record.ExceptionAsync(() => data.Service.RequestAsync(data.Maker, second, other["binding"].AsString, D.Meta("race-two"))));
        Assert.Single(outcomes, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, error => error is not null));
        var winnerKey = outcomes[0] is null ? "race-one" : "race-two"; var winnerInput = outcomes[0] is null ? input : second; var winnerPreview = outcomes[0] is null ? preview : other;
        var recovered = Assert.IsType<BsonDocument>(await data.Service.LookupAsync(data.Maker, D.RequestOperation, winnerKey)); var id = recovered["claimId"].AsInt64;
        var committed = await data.StateAsync(); Assert.Equal(recovered, await data.Service.RequestAsync(data.Maker, winnerInput, winnerPreview["binding"].AsString, D.Meta(winnerKey)));
        Assert.Equal(committed, await data.StateAsync());
        foreach (var (table, filter) in new[] {
            ("auditEvents", new BsonDocument("kind", D.RequestOperation)), ("outboxEvents", new BsonDocument("kind", D.RequestOperation)),
            ("idempotencyRecords", new BsonDocument("operation", D.RequestOperation)),
            ("supplierClaimRequests", new BsonDocument("_id", id)), ("procurementQuarantineDispositionHeads", new BsonDocument()), ("procurementQuarantineDispositions", new BsonDocument()),
            ("procurementInspectionSubmissions", new BsonDocument("_id", data.InspectionId)) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try {
                await DeniedAsync(() => data.Service.ReadAsync(data.Reader, id)); await DeniedAsync(() => data.Service.LookupAsync(data.Maker, D.RequestOperation, winnerKey));
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { ClaimReference = "AFTER-MISSING-PROOF", Lines = [input.Lines[0] with { Packages = 1 }] }));
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.Equal(recovered, await data.Service.LookupAsync(data.Maker, D.RequestOperation, winnerKey)); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Foreign_scope_expired_factor_and_revoked_grant_cannot_escape_an_active_claim_review() {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        var request = await data.RequestAsync("grant-race-source"); var id = request["claimId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, id));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "supplierClaimRequests" && requestId == 0) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Supplier claim authority race did not complete");
            } });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(db, unit, new(db), data.Clock); var orders = new PurchaseOrderService(db, unit, new(db), data.Clock, catalog,
            new(db, unit, new(db), data.Clock, catalog), new(db, unit, new(db), data.Clock, catalog));
        var service = new SupplierClaimService(db, unit, new(db), data.Clock, catalog, orders,
            new(db, unit, new(db), data.Clock, catalog, orders, new(db, unit, new(db), data.Clock, catalog)));
        var pending = Task.Run(() => service.PreviewDecisionAsync(data.Checker, id, SupplierClaimDecision.Approve));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, (await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"))).ModifiedCount);
        } finally { release.Set(); }
        await DeniedAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(60))); Assert.Equal(1, observed);
        Assert.True((await data.Service.ReadAsync(data.Reader, id))["decision"].IsBsonNull); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public void Claim_inputs_require_actual_quantity_consistent_basis_explicit_deadline_and_complete_optional_invoice_identity() {
        var input = new SupplierClaimInput(ProcurementRequestTestData.Location, 1, 1, 1, "000claim", new(2026, 10, 3), new(2026, 10, 10),
            "Explicit synthetic policy", SupplierClaimResolution.Investigation, D.Reason, [new(SupplierClaimBasis.PhysicalReceived, 1, 1, 1, "other", null, null, null, null)]);
        Assert.Equal("000claim", SupplierClaimRules.Copy(input).ClaimReference);
        foreach (var invalid in new[] { input with { ResponseDueOn = input.BusinessDate.AddDays(-1) }, input with { DeadlineBasisReference = "" },
            input with { Lines = [input.Lines[0] with { Packages = 0 }] }, input with { Lines = [input.Lines[0], input.Lines[0]] },
            input with { Lines = [input.Lines[0] with { Basis = SupplierClaimBasis.ShortageObserved }] },
            input with { Lines = [input.Lines[0] with { InvoiceDocumentId = 3 }] }, input with { Lines = [input.Lines[0] with { InvoiceMatchId = 4, InvoiceMatchLineNumber = 1 }] } })
            Assert.Throws<DomainValidationException>(() => SupplierClaimRules.Copy(invalid));
    }

    private static async Task DeniedAsync(Func<Task> action) { var error = await Record.ExceptionAsync(action); Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, error?.ToString() ?? "Unexpected success"); }
    private static Task GuardAsync(D data, BsonDocument validator) => data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", "outboxEvents" }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
}

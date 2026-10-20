using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Inventory;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;
using D = UnitToko.Tests.Billing.InvoiceReleaseTestData;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class InvoiceReleaseTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_independent_release_issues_once_from_received_goods_and_preserves_stock_cash_and_original_COGS() {
        await using var data = await D.CreateAsync(fixture); var original = await data.ProtectedAsync();
        Assert.NotEmpty(original["cogsJournals"].AsBsonArray);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Invoices.PreviewAsync(data.Finance, data.Selection));
        var proposal = await data.ProposeAsync();
        Assert.Equal(proposal, await data.Releases.RecoverAsync(data.Maker, "propose", "release-propose"));
        await data.Dispatch.Base.AddGrantAsync(data.Maker, 811690, new(UnitToko.Modules.Organizations.GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), [Permissions.InvoiceReleaseApprove]);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Releases.PreviewDecisionAsync(data.Maker, proposal.ProposalId.Value, 1, InvoiceReleaseDecision.Approve))).Code);
        var approved = await data.DecideAsync(proposal);
        Assert.Equal(approved, await data.Releases.RecoverAsync(data.Checker, "approve", "release-approve"));
        Assert.Equal(original, await data.ProtectedAsync());
        Assert.False(await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).AnyAsync());
        var preview = await data.Invoices.PreviewAsync(data.Finance, data.Selection);
        Assert.Equal(14, preview.TermDays); Assert.Equal(preview.IssueDate.AddDays(14), preview.DueDate);
        var meta = D.Meta("released-invoice"); var issued = await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, meta);
        Assert.Equal(issued.Id, (await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, meta)).Id);
        Assert.Equal(issued.Id, (await data.Invoices.RecoverAsync(data.Finance, meta.IdempotencyKey))!.Id);
        var invoice = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
        var binding = await data.Rows(B.Bindings).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
        Assert.Equal("approved", binding["kind"].AsString); Assert.Equal(approved.RecordId.Value, binding["decisionId"].AsInt64);
        Assert.Equal(invoice["snapshotHash"], binding["invoiceHash"]);
        Assert.Equal(original, await data.ProtectedAsync());
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.invoice")));
        Assert.Equal(1, await data.Rows(InvoiceBson.Invoices).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(InvoiceBson.Allocations).CountDocumentsAsync(new BsonDocument()));
        Assert.Empty((await data.Releases.CandidatesAsync(data.Outsider)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Invoices.ReadAsync(data.Outsider, issued.Id.Value));
        var file = await data.Invoices.DownloadAsync(data.Finance, issued.Id.Value); Assert.Equal(issued.DocumentHash, file.Hash);
        // Historical recovery does not ask for a new tax/price/release approval after expiry.
        data.Clock.UtcNow = data.Clock.UtcNow.AddHours(2);
        data.Finance = await data.Dispatch.IssueExistingSessionAsync(data.Finance);
        Assert.Equal(issued.Id, (await data.Invoices.RecoverAsync(data.Finance, meta.IdempotencyKey))!.Id);
        await data.Rows(B.Bindings).DeleteOneAsync(new BsonDocument("_id", issued.Id.Value));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Invoices.RecoverAsync(data.Finance, meta.IdempotencyKey));
        Assert.Equal(1, await data.Rows(InvoiceBson.Invoices).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Reject_is_durable_and_changed_source_or_budget_cannot_bypass_fresh_independent_review() {
        await using var data = await D.CreateAsync(fixture); var original = await data.ProtectedAsync();
        var proposal = await data.ProposeAsync(); var rejected = await data.DecideAsync(proposal, "release-reject", InvoiceReleaseDecision.Reject);
        Assert.Equal("reject", rejected.Action); Assert.Equal(rejected, await data.Releases.RecoverAsync(data.Checker, "reject", "release-reject"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Invoices.PreviewAsync(data.Finance, data.Selection));
        var tooMuch = data.Input with { ExpectedReleaseRevision = 2, Allocations = [data.Input.Allocations[0] with { Tax = new(data.Input.Allocations[0].Tax.Value + .01m) }] };
        Assert.Equal("invoice_commercial_allocation_exceeded", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Releases.PreviewAsync(data.Maker, tooMuch))).Code);
        var next = await data.ProposeAsync("release-next", data.Input with { ExpectedReleaseRevision = 2 });
        var preview = await data.Releases.PreviewDecisionAsync(data.Checker, next.ProposalId.Value, 3, InvoiceReleaseDecision.Approve);
        var receipt = await data.Rows("shipmentReceipts").Find(new BsonDocument("_id", data.Receipt.Id.Value)).SingleAsync();
        await data.Rows("shipmentReceipts").DeleteOneAsync(new BsonDocument("_id", data.Receipt.Id.Value));
        // A receipt removed while its confirmation effects remain is tampering: the receipt owner refuses before any decision.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Releases.DecideAsync(data.Checker, next.ProposalId.Value, 3, InvoiceReleaseDecision.Approve, preview.Binding, D.Meta("damaged-source")));
        await data.Rows("shipmentReceipts").InsertOneAsync(receipt);
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("key", "damaged-source")).AnyAsync());
        var stored = await data.Releases.ReadAsync(data.Reader, next.RecordId.Value); Assert.Equal(next, stored.Result);
        Assert.Equal(original, await data.ProtectedAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Releases.ReadAsync(data.Outsider, next.RecordId.Value));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Releases.ProposeAsync(data.Maker, data.Input with { ExpectedReleaseRevision = 2 },
            new string('a', 64), D.Meta("release-next")));
        Assert.Equal(3, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Original_invoice_binding_cannot_hide_a_forged_new_allocation_or_be_recreated_after_deletion() {
        await using var data = await D.CreateAsync(fixture); await data.DecideAsync(await data.ProposeAsync());
        var preview = await data.Invoices.PreviewAsync(data.Finance, data.Selection);
        var issued = await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, D.Meta("bound-invoice"));
        var invoice = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
        var allocation = await data.Rows(InvoiceBson.Allocations).Find(new BsonDocument("invoiceId", issued.Id.Value)).SingleAsync();
        var forged = allocation.DeepClone().AsBsonDocument; forged["tax"] = new BsonDecimal128(0m);
        await data.Rows(InvoiceBson.Allocations).ReplaceOneAsync(new BsonDocument("_id", allocation["_id"]), forged);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var authority = await data.Dispatch.Base.Store.LoadAsync(data.Finance, tx, ct);
            _ = await new InvoiceReleaseSources(data.Database, data.Clock, data.Receipts).CaptureAsync(data.Finance, authority, tx,
                data.Input, Permissions.InvoiceIssue, ct, invoice); return true;
        }));
        await data.Rows(InvoiceBson.Allocations).ReplaceOneAsync(new BsonDocument("_id", allocation["_id"]), allocation);
        var binding = await data.Rows(B.Bindings).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
        var wrong = invoice.DeepClone().AsBsonDocument; wrong["issuedBy"] = data.Maker.UserId;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var authority = await data.Dispatch.Base.Store.LoadAsync(data.Finance, tx, ct);
            _ = await new InvoiceReleaseSources(data.Database, data.Clock, data.Receipts).CaptureAsync(data.Finance, authority, tx,
                data.Input, Permissions.InvoiceIssue, ct, wrong); return true;
        }));
        await data.Rows(B.Bindings).DeleteOneAsync(new BsonDocument("_id", issued.Id.Value));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, D.Meta("bound-invoice")));
        Assert.False(await data.Rows(B.Bindings).Find(new BsonDocument("_id", issued.Id.Value)).AnyAsync());
        Assert.Equal(invoice, await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync());
        Assert.Equal("approved", binding["kind"].AsString);
    }
}

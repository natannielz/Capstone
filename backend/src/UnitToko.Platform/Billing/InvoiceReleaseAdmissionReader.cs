using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Fulfillment;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

internal interface IInvoiceReleaseWriter
{
    Task BindPostedAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms, BsonDocument invoice, CancellationToken ct);
    void RequireCurrent(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms);
}

public sealed partial class InvoiceReleaseService
{
    internal IInvoiceAdmissionReader CreateReader() => new ApprovedReader(this);
    private sealed class ApprovedReader(InvoiceReleaseService owner) : IInvoiceAdmissionReader, IInvoiceReleaseWriter
    {
        // Captures are private to the caller's actual transaction handle, never a
        // process-wide last approval or an HTTP-supplied exclusion identifier.
        private readonly ConditionalWeakTable<IClientSessionHandle, IssueCapture> captures = new();
        public IMongoDatabase Database => owner.database;
        public async Task<InvoiceTerms> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, ImmutableArray<InvoiceReceiptSource> expected,
            DateOnly issueDate, CancellationToken ct) {
            await owner.StorageAsync(ct);
            var authority = await owner.authorization.LoadAsync(actor, tx, ct);
            var selected = InvoicePolicy.Copy(expected.Select(source => new InvoiceReceiptSelection(new(source.Snapshot["shipment"]["shipmentId"].AsInt64), new(source.Snapshot["receipt"]["id"].AsInt64))));
            var historical = await owner.records.SelectionAsync(tx, selected, ct) ?? throw B.Changed();
            if (historical.Latest["action"] != "approve") throw B.Changed();
            var input = B.Input(historical.Proposal["input"]);
            var source = await owner.sources.CaptureAsync(actor, authority, tx, input, Permissions.InvoiceIssue, ct);
            InvoiceReleaseRecords.Equal(InvoiceBson.Sources(expected), InvoiceBson.Sources(source.Receipts));
            InvoiceReleaseRecords.Equal(historical.Latest["source"], source.Snapshot);
            if (issueDate != AccountingCalendar.BusinessDate(source.CapturedAt)) throw B.Changed();
            var context = new Context(actor, authority, tx, Permissions.InvoiceIssue, source.Receipts, source.CapturedAt) { History = historical, Capture = source };
            await owner.PeopleAsync(context, historical.Proposal, historical.Latest, ct);
            var terms = InvoiceReleaseSources.Terms(historical.Head["_id"].AsInt64, historical.Latest["revision"].AsInt64, input, source.Snapshot);
            InvoiceAdmissionPolicy.Require(source.Receipts, terms, owner.clock.UtcNow);
            await owner.records.FenceAsync(tx, historical, ct);
            await owner.receipts.InvoiceFenceAsync(actor, authority, tx, source.Receipts.Select(source => source.Resource), Permissions.InvoiceIssue, ct);
            await owner.SessionAsync(context, ct); owner.Current(context); captures.Remove(tx); captures.Add(tx, new(context, terms)); return terms;
        }
        public async Task BindPostedAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms, BsonDocument invoice, CancellationToken ct) {
            var capture = Require(tx, actor, terms);
            if (capture.Invoice is not null) throw B.Broken();
            if (invoice["issuedBy"] != actor.UserId || !InvoiceRecords.Equal(invoice["terms"], InvoiceBson.Terms(terms)) ||
                !InvoiceRecords.Equal(invoice["sources"], InvoiceBson.Sources(capture.Context.Receipts))) throw B.Broken();
            // Verify the actual original invoice/audit/receipt/journal/allocation
            // graph first. Only this exact newly posted result may be excluded from
            // the subsequent comparison with the pre-issuance allocation history.
            await InvoiceRecords.VerifyLegacyPersistedAsync(owner.database, tx, invoice, ct);
            await owner.records.BindInvoiceAsync(tx, invoice, capture.Context.History!, ct);
            await owner.records.VerifyInvoiceBindingAsync(tx, invoice, ct);
            capture.Invoice = invoice.DeepClone().AsBsonDocument;
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms approved, CancellationToken ct) {
            var capture = Require(tx, actor, approved); var context = capture.Context;
            if (capture.Invoice is { } invoice) {
                await InvoiceRecords.VerifyLegacyPersistedAsync(owner.database, tx, invoice, ct);
                await owner.records.VerifyInvoiceBindingAsync(tx, invoice, ct);
            }
            var fresh = await owner.sources.CaptureAsync(actor, context.Authority, tx, context.Capture!.Input, Permissions.InvoiceIssue, ct, capture.Invoice);
            InvoiceReleaseRecords.Equal(fresh.Snapshot, context.Capture.Snapshot);
            await owner.records.FenceAsync(tx, context.History!, ct);
            await owner.PeopleAsync(context, context.History!.Proposal, context.History.Latest, ct);
            await owner.receipts.InvoiceFenceAsync(actor, context.Authority, tx, context.Receipts.Select(source => source.Resource), Permissions.InvoiceIssue, ct);
            await owner.SessionAsync(context, ct); owner.Current(context); InvoiceAdmissionPolicy.Current(approved, owner.clock.UtcNow); ct.ThrowIfCancellationRequested();
        }
        private IssueCapture Require(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms) {
            if (!captures.TryGetValue(tx, out var capture) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, owner.database.Client) ||
                capture.Context.Actor != actor || !InvoiceRecords.Equal(InvoiceBson.Terms(capture.Terms), InvoiceBson.Terms(terms))) throw B.Broken();
            owner.Current(capture.Context); return capture;
        }
        public void RequireCurrent(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms) { _ = Require(tx, actor, terms); }
        private sealed class IssueCapture(Context context, InvoiceTerms terms) {
            internal Context Context { get; } = context;
            internal InvoiceTerms Terms { get; } = terms;
            internal BsonDocument? Invoice { get; set; }
        }
    }
}

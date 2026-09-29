using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

public sealed record InvoiceReadLine(LongId OrderId, LongId ShipmentId, LongId ReceiptId, LongId SkuId, string SkuCode, string ProductName,
    string PackageLabel, int BaseUnitsPerPackage, string BaseUnitCode, long AcceptedBaseUnits, Money Net, Money Tax, Money Charges);
public sealed record InvoiceIssuePreview(string Review, string BuyerName, string SellerName, DateOnly IssueDate, DateOnly DueDate,
    string Grouping, DateOnly PeriodFrom, DateOnly PeriodTo, string TermOrigin, int TermDays, string DayRule, string TaxDocumentProcess,
    Money Net, Money Tax, Money Charges, Money Total, ImmutableArray<InvoiceReadLine> Lines);
public sealed record InvoiceView(LongId Id, LongId NumberEntity, LongId NumberChannel, int NumberYear, long Number, DateTimeOffset IssuedAt,
    InvoiceIssuePreview Detail, string DocumentHash, int DocumentLength);

public sealed partial class InvoiceService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly ShipmentReceiptService receipts;
    private readonly IInvoiceAdmissionReader? admission;
    private readonly AuthorizationStore authorization;
    private readonly InvoiceRecords records;
    public InvoiceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ShipmentReceiptService receipts) : this(database, unit, ids, clock, receipts, null) { }
    internal InvoiceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ShipmentReceiptService receipts, IInvoiceAdmissionReader? admission) {
        foreach (var source in new[] { receipts.SourceDatabase, admission?.Database }.Where(source => source is not null))
            if (!ReferenceEquals(source!.Client, database.Client) || !source.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw new ArgumentException("Invoice owners must share one database transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.receipts = receipts; this.admission = admission;
        authorization = new(database, unit, clock); records = new(database, receipts);
    }
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private async Task StorageAsync(CancellationToken ct) { await receipts.InvoiceStorageAsync(ct); await InvoiceStorage.RequireAsync(database, ct); }
    private static InvoiceIssuePreview Preview(BsonArray sources, BsonDocument terms, BsonArray lines, DateOnly date, DateOnly due, string review) {
        var total = InvoiceBson.Totals(lines); var rules = InvoiceBson.Terms(terms);
        return new(review, sources[0]["snapshot"]["commercial"]["terms"]["buyer"]["name"].AsString, sources[0]["snapshot"]["shipment"]["seller"]["name"].AsString,
            date, due, rules.Grouping, rules.PeriodFrom, rules.PeriodTo, rules.TermOrigin, rules.TermDays, rules.DayRule, rules.TaxDocumentProcess,
            InvoiceBson.Money(total["net"]), InvoiceBson.Money(total["tax"]), InvoiceBson.Money(total["charges"]), InvoiceBson.Money(total["total"]),
            [.. lines.Select(line => new InvoiceReadLine(new(line["orderId"].AsInt64), new(line["shipmentId"].AsInt64), new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64),
                line["skuCode"].AsString, line["productName"].AsString, line["definition"]["packageLabel"].AsString, line["definition"]["baseUnitsPerPackage"].AsInt32,
                line["definition"]["baseUnitCode"].AsString, line["acceptedBaseUnits"].AsInt64, InvoiceBson.Money(line["net"]), InvoiceBson.Money(line["tax"]), InvoiceBson.Money(line["charges"])))]);
    }
    private static InvoiceView View(BsonDocument row) => new(new(row["_id"].AsInt64), new(row["numberEntity"].AsInt64), new(row["numberChannel"].AsInt64), checked((int)row["numberYear"].AsInt64),
        row["number"].AsInt64, ShipmentReceiptRecords.Time(row["issuedAt"]), Preview(row["sources"].AsBsonArray, row["terms"].AsBsonDocument, row["lines"].AsBsonArray,
            InvoiceBson.Date(row["issueDate"]), InvoiceBson.Date(row["dueDate"]), row["review"].AsString), row["documentHash"].AsString, row["documentLength"].AsInt32);
    private async Task<ImmutableArray<InvoiceReceiptSource>> SourcesAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ImmutableArray<InvoiceReceiptSelection> selected, string permission, CancellationToken ct) {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, clock.UtcNow));
        var result = ImmutableArray.CreateBuilder<InvoiceReceiptSource>();
        foreach (var item in selected) {
            var source = await receipts.InvoiceSourceAsync(actor, authority, tx, item.ShipmentId.Value, permission, ct) ?? throw InvoiceRecords.Invalid();
            if (source.Snapshot["receipt"]["id"] != item.ReceiptId.Value || source.Snapshot["receipt"]["lines"].AsBsonArray.Count == 0) throw InvoiceRecords.Invalid();
            result.Add(source);
        }
        if (result.Sum(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Count) > InvoicePolicy.MaximumLines) throw InvoicePolicy.Invalid();
        return result.ToImmutable();
    }
    private async Task<(InvoiceTerms Terms, BsonArray Lines, DateOnly Date, DateOnly Due, string Review)> PrepareAsync(ActorContext actor, IClientSessionHandle tx, ImmutableArray<InvoiceReceiptSource> sources, CancellationToken ct) {
        if (admission is null) throw InvoicePolicy.Invalid(); var now = Now; var date = AccountingCalendar.BusinessDate(now);
        var terms = await admission.ReadAndFenceAsync(tx, actor, sources, date, ct); InvoiceAdmissionPolicy.Require(sources, terms, now);
        var lines = new BsonArray();
        foreach (var order in sources.GroupBy(source => source.Snapshot["commercial"]["orderId"].AsInt64)) {
            var previous = await records.OrderAllocationsAsync(actor, tx, order.Key, ct); var accumulated = previous.Select(row => row.DeepClone().AsBsonDocument).ToList();
            foreach (var source in order) foreach (var accepted in source.Snapshot["receipt"]["lines"].AsBsonArray) {
                if (accumulated.Any(row => row["receiptId"] == source.Snapshot["receipt"]["id"] && row["skuId"] == accepted["skuId"]))
                    throw new PersistenceConflictException("receipt_already_invoiced", "Accepted receipt quantities are already invoiced");
                var existing = accumulated.Where(row => row["skuId"] == accepted["skuId"]).ToArray();
                if (existing.Any(row => row["rounding"] != terms.Rounding)) throw InvoicePolicy.Invalid("invoice_rounding_changed");
                var units = existing.Aggregate(0L, (total, row) => checked(total + row["acceptedBaseUnits"].AsInt64));
                var net = existing.Aggregate(new Money(0m), (total, row) => Money.Add(total, InvoiceBson.Money(row["net"])));
                var line = InvoiceRecords.Line(source, terms, accepted["skuId"].AsInt64, units, net); lines.Add(line);
                var projection = line.DeepClone().AsBsonDocument; projection.Add("rounding", terms.Rounding); accumulated.Add(projection);
            }
            foreach (var amount in new[] { "tax", "charges" }) {
                var allocated = accumulated.Aggregate(new Money(0m), (total, row) => Money.Add(total, InvoiceBson.Money(row[amount])));
                if (allocated.Value > InvoiceBson.Money(order.First().Snapshot["commercial"]["terms"][amount]).Value) throw InvoicePolicy.Invalid("invoice_commercial_allocation_exceeded");
            }
        }
        _ = InvoiceBson.Totals(lines); var due = InvoiceAdmissionPolicy.DueDate(sources, terms, date);
        return (terms, lines, date, due, InvoiceBson.Review(InvoiceBson.Sources(sources), InvoiceBson.Terms(terms), lines, date, due));
    }
    public async Task<InvoiceIssuePreview> PreviewAsync(ActorContext actor, IEnumerable<InvoiceReceiptSelection> selected, CancellationToken ct = default) {
        var copied = InvoicePolicy.Copy(selected); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var sources = await SourcesAsync(actor, authority, tx, copied, Permissions.InvoiceIssue, token); var prepared = await PrepareAsync(actor, tx, sources, token);
            await admission!.CompleteAsync(tx, actor, prepared.Terms, token); InvoiceAdmissionPolicy.Current(prepared.Terms, clock.UtcNow);
            await receipts.InvoiceFenceAsync(actor, authority, tx, sources.Select(source => source.Resource), Permissions.InvoiceIssue, token);
            return Preview(InvoiceBson.Sources(sources), InvoiceBson.Terms(prepared.Terms), prepared.Lines, prepared.Date, prepared.Due, prepared.Review);
        }, ct);
    }
    public async Task<InvoiceView> IssueAsync(ActorContext actor, IEnumerable<InvoiceReceiptSelection> selected, string review, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        var copied = InvoicePolicy.Copy(selected); AuthorizationStore.ValidateMetadata(metadata);
        if (review is null || !Regex.IsMatch(review, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)) throw InvoicePolicy.Invalid();
        await StorageAsync(ct); var id = (await ids.NextAsync(InvoiceBson.Invoices, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = InvoiceBson.Body(copied, review); ImmutableArray<InvoiceReceiptSource> sources = []; InvoiceTerms? approved = null; BsonDocument final = null!;
        await authorization.ExecuteCommandAsync(actor, InvoiceBson.Operation, metadata, body, async (authority, tx, token) => {
            approved = null; sources = await SourcesAsync(actor, authority, tx, copied, Permissions.InvoiceIssue, token);
            foreach (var source in sources) if (!ScopePolicy.Authorize(actor, authority, Permissions.InvoiceIssue, source.Resource, clock.UtcNow).Allowed) throw InvoiceRecords.Invalid();
            return new(ScopePolicy.Authorize(actor, authority, Permissions.InvoiceIssue, sources[0].Resource, clock.UtcNow),
                [.. sources.SelectMany(source => ScopePolicy.RequiredNodes(source.Resource, authority.Directory)).Distinct()], Permissions.InvoiceIssue, new(sources[0].Resource));
        }, async (tx, token) => {
            var prepared = await PrepareAsync(actor, tx, sources, token);
            if (prepared.Review != review) throw new PersistenceConflictException("invoice_review_changed", "Reload the invoice review before confirming");
            approved = prepared.Terms; var now = Now;
            if (AccountingCalendar.BusinessDate(now) != prepared.Date) throw InvoicePolicy.Invalid("invoice_review_changed");
            foreach (var line in prepared.Lines) line.AsBsonDocument.Add("allocationId", (await ids.NextAsync(InvoiceBson.Allocations, token)).Value);
            var entity = sources[0].Resource.FulfillmentOrganizationId!.Value; var channel = sources[0].Resource.ChannelId!.Value;
            var number = await NextNumberAsync(tx, entity, channel, prepared.Date.Year, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "issuedAt", now.UtcDateTime }, { "issueDate", InvoiceBson.Date(prepared.Date) }, { "dueDate", InvoiceBson.Date(prepared.Due) },
                { "numberEntity", entity }, { "numberChannel", channel }, { "numberYear", (long)prepared.Date.Year }, { "number", number }, { "issuedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "sources", InvoiceBson.Sources(sources) }, { "scopes", new BsonArray(sources.Select(source => ShipmentPreparationBson.Scope(source.Resource))) },
                { "orderIds", new BsonArray(sources.Select(source => source.Snapshot["commercial"]["orderId"].AsInt64).Distinct().Order().Select(value => new BsonInt64(value))) },
                { "terms", InvoiceBson.Terms(approved) }, { "lines", prepared.Lines }, { "totals", InvoiceBson.Totals(prepared.Lines) }, { "review", review },
                { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "snapshotHash", BsonNull.Value }, { "postingFence", 0L },
                { "auditId", audit }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
            var html = InvoiceRenderer.Render(row); row.Add("document", html); row.Add("documentHash", InvoiceRenderer.Hash(html)); row.Add("documentLength", Encoding.UTF8.GetByteCount(html));
            if (row["documentLength"].AsInt32 > 2097152) throw InvoicePolicy.Invalid("invoice_document_too_large");
            await records.Rows(InvoiceBson.Invoices).InsertOneAsync(tx, row, cancellationToken: token);
            foreach (var line in prepared.Lines) await records.Rows(InvoiceBson.Allocations).InsertOneAsync(tx, InvoiceBson.Allocation(row, line.AsBsonDocument), cancellationToken: token);
            var journal = await new PostJournal(database, clock, [new InvoicePostingSource(database, row)]).PostAsync(tx, actor, InvoicePostingSource.Source(id), 1, new(metadata.Reason, metadata.CorrelationId), token);
            row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["postingFence"] = 1L; row["snapshotHash"] = InvoiceBson.Hash(row);
            if ((await records.Rows(InvoiceBson.Invoices).ReplaceOneAsync(tx, new BsonDocument { { "_id", id }, { "postingFence", 1L }, { "journalId", BsonNull.Value } }, row, cancellationToken: token)).ModifiedCount != 1) throw InvoiceRecords.Invalid();
            await records.Rows("auditEvents").InsertOneAsync(tx, InvoiceBson.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", InvoiceBson.Operation },
                { "sourceKey", InvoiceBson.Operation + ":" + id }, { "payload", new BsonDocument("invoiceId", id) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return InvoiceBson.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await records.Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument { { "issuedBy", actor.UserId }, { "requestKey", metadata.IdempotencyKey } }).SingleOrDefaultAsync(token) ?? throw InvoiceRecords.Invalid();
            final = await records.VerifyAsync(actor, tx, original, token);
            if (final["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) || !InvoiceRecords.Equal(final["sources"], InvoiceBson.Sources(sources))) throw InvoiceRecords.Invalid();
            if (approved is not null) { await admission!.CompleteAsync(tx, actor, approved, token); InvoiceAdmissionPolicy.Current(approved, clock.UtcNow); }
            await receipts.InvoiceFenceAsync(actor, authority, tx, sources.Select(source => source.Resource), Permissions.InvoiceIssue, token);
        });
        return View(final);
    }
    private async Task<long> NextNumberAsync(IClientSessionHandle tx, long entity, long channel, int year, CancellationToken ct) {
        var key = FormattableString.Invariant($"invoice-number.{entity}.{channel}.{year}"); var rows = records.Rows("idCounters");
        var current = await rows.Find(tx, new BsonDocument("_id", key)).SingleOrDefaultAsync(ct);
        var latest = await records.Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument { { "numberEntity", entity }, { "numberChannel", channel }, { "numberYear", (long)year } }).Sort(new BsonDocument("number", -1)).FirstOrDefaultAsync(ct);
        if (current is null) {
            if (latest is not null) throw InvoiceRecords.Invalid();
            await rows.InsertOneAsync(tx, new BsonDocument { { "_id", key }, { "value", 1L } }, cancellationToken: ct); return 1;
        }
        if (!current["value"].IsInt64 || current["value"].AsInt64 is < 1 or long.MaxValue || latest is null || latest["number"] != current["value"]) throw InvoiceRecords.Invalid();
        if ((await rows.UpdateOneAsync(tx, current, Builders<BsonDocument>.Update.Inc("value", 1L), cancellationToken: ct)).ModifiedCount != 1) throw InvoiceRecords.Invalid();
        return checked(current["value"].AsInt64 + 1);
    }
}

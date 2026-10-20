using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

internal sealed class InvoiceReleaseAccounting(IMongoDatabase database, IClock clock)
{
    internal async Task<BsonDocument> ReadAndFenceAsync(IClientSessionHandle tx, OperationalBookKey key,
        InvoiceReleaseMappings requested, DateOnly date, CancellationToken ct)
    {
        AccountingPersistence.RequireSession(database, tx); AccountingBookSetupRules.Key(key);
        var began = clock.UtcNow; if (date > AccountingCalendar.BusinessDate(began)) throw B.Changed();
        var setup = new AccountingBookSetupRecords(database);
        var proposal = await setup.ApprovedAsync(tx, key, ct) ?? throw B.Changed();
        var decision = await setup.DecisionForAsync(tx, proposal, ct) ?? throw B.Changed();
        if (decision["decision"] != "approve") throw B.Changed();
        var persistence = new AccountingPersistence(database, clock);
        var book = await persistence.BookAsync(tx, key, ct);
        if (book["_id"] != decision["bookId"] || book["chartVersionId"] != decision["chartId"] || book["key"] != decision["key"]) throw B.Broken();
        var chart = await persistence.Collection("accountingChartVersions").Find(tx, new BsonDocument {
            { "_id", book["chartVersionId"] }, { "bookId", book["_id"] }, { "active", true }
        }).SingleOrDefaultAsync(ct) ?? throw B.Changed();
        var mappings = AccountingBson.ReadChart(chart).Mappings;
        var accounts = new BsonArray();
        foreach (var item in B.Mappings(requested)) {
            var mapping = mappings.SingleOrDefault(value => value.MappingKey == item.Value.AsString && value.Active) ?? throw B.Changed();
            accounts.Add(new BsonDocument { { "role", item.Name }, { "mappingKey", mapping.MappingKey },
                { "accountId", mapping.AccountId }, { "accountCode", mapping.AccountCode } });
        }
        var month = AccountingCalendar.Month(date); var period = await persistence.PeriodAsync(tx, book["_id"].AsInt64, month, ct);
        AccountingPersistence.RequireOpen(book, period, month);
        if (period["state"] != "open" || period["bookId"] != book["_id"] || period["month"] != month) throw B.Changed();
        // These are the real reviewed setup records. The capture changes no account,
        // period or posting amount; exact immutable rewrites fence concurrent removal.
        await InvoiceReleaseRecords.FenceFrozenAsync(database, tx, "accountingChartVersions", chart, ct);
        await FenceApprovedAsync(database, tx, proposal, decision, ct);
        await InvoiceReleaseRecords.FenceFrozenAsync(database, tx, "periods", period, ct);
        await persistence.TouchBookAsync(tx, book, ct);
        if (clock.UtcNow < began || decision["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Changed();
        var stableBook = book.DeepClone().AsBsonDocument; stableBook.Remove("fence");
        return new() { { "key", AccountingBson.Key(key) }, { "book", stableBook }, { "chart", chart },
            { "setupProposalId", proposal["_id"] }, { "setupProposalHash", proposal["snapshotHash"] },
            { "setupDecisionId", decision["_id"] }, { "setupDecisionHash", decision["snapshotHash"] },
            { "accounts", accounts }, { "periodId", period["_id"] }, { "periodMonth", month } };
    }
    internal static async Task FenceApprovedAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument proposal, BsonDocument decision, CancellationToken ct) {
        foreach (var (name, row) in new[] { (AccountingBookSetupBson.Proposals, proposal), (AccountingBookSetupBson.Decisions, decision) }) {
            await InvoiceReleaseRecords.FenceFrozenAsync(database, tx, name, row, ct);
            await InvoiceReleaseRecords.FenceOriginalCommandAsync(database, tx, row, ct);
        }
    }
}

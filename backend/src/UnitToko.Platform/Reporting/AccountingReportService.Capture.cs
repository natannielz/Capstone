using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Reporting;

public sealed partial class AccountingReportService
{
    private sealed record Capture(AccountingReportBook Book, string EndDate, string Hash, string BookHash, string ChartSetHash,
        BsonArray Selection, Money Debit, Money Credit, IReadOnlyList<AccountingReportAccount> Accounts, IReadOnlyList<AccountingReportJournal> Journals)
    {
        internal AccountingReportSnapshot Snapshot(LongId id, DateTimeOffset capturedAt, DateTimeOffset expiresAt) =>
            new(id, Hash, Book, EndDate, capturedAt, expiresAt, Debit, Credit, Accounts, Journals);
    }
    private async Task<BsonDocument> CurrentChartAsync(Context context, CancellationToken ct)
    {
        var filter = new BsonDocument("_id", context.Book["chartVersionId"]);
        await ValidRowsAsync("accountingChartVersions", filter, AccountingSchemas.Validator("accountingChartVersions"), context.Tx, ct);
        var chart = await Rows("accountingChartVersions").Find(context.Tx, filter).SingleOrDefaultAsync(ct) ?? throw AccountingReportRecords.Broken();
        if (chart["bookId"] != context.Book["_id"] || !chart["active"].AsBoolean) throw AccountingReportRecords.Broken();
        _ = AccountingBson.ReadChart(chart); return chart;
    }
    private async Task<Capture> CaptureAsync(Context context, string endDate, IReadOnlyList<BsonDocument> selected, CancellationToken ct)
    {
        try { return await CaptureCoreAsync(context, endDate, selected, ct); }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException or KeyNotFoundException) { throw AccountingReportRecords.Broken(); }
        catch (DomainValidationException error) when (error.Code != "accounting_report_too_large") { throw AccountingReportRecords.Broken(); }
    }
    private async Task<Capture> CaptureCoreAsync(Context context, string endDate, IReadOnlyList<BsonDocument> selected, CancellationToken ct)
    {
        if (selected.Count > AccountingReportRules.MaximumJournals || selected.Sum(row => row["lines"].AsBsonArray.Count) > AccountingReportRules.MaximumLines)
            throw AccountingReportRules.TooLarge();
        var bookId = context.Book["_id"].AsInt64;
        var journalMap = selected.ToDictionary(row => row["_id"].AsInt64);
        var originalIds = selected.Select(row => AccountingBson.NullableId(row["reversalOf"])).OfType<long>().Where(id => !journalMap.ContainsKey(id)).Distinct().ToArray();
        if (originalIds.Length > 0) {
            var filter = new BsonDocument("_id", new BsonDocument("$in", new BsonArray(originalIds)));
            await ValidRowsAsync("journalEntries", filter, AccountingSchemas.Validator("journalEntries"), context.Tx, ct);
            var originals = await Rows("journalEntries").Find(context.Tx, filter).Limit(AccountingReportRules.MaximumJournals + 1).ToListAsync(ct);
            if (originals.Count != originalIds.Length) throw AccountingReportRecords.Broken();
            foreach (var original in originals) journalMap.Add(original["_id"].AsInt64, original);
        }
        if (journalMap.Values.Sum(row => row["lines"].AsBsonArray.Count) > AccountingReportRules.MaximumLines * 2) throw AccountingReportRules.TooLarge();
        var currentChart = await CurrentChartAsync(context, ct);
        var chartIds = journalMap.Values.Select(row => row["chartId"].AsInt64).Append(currentChart["_id"].AsInt64).Distinct().ToArray();
        if (chartIds.Length > AccountingReportRules.MaximumCharts) throw AccountingReportRules.TooLarge();
        var chartsFilter = new BsonDocument("_id", new BsonDocument("$in", new BsonArray(chartIds)));
        await ValidRowsAsync("accountingChartVersions", chartsFilter, AccountingSchemas.Validator("accountingChartVersions"), context.Tx, ct);
        var chartRows = await Rows("accountingChartVersions").Find(context.Tx, chartsFilter).Sort(new BsonDocument("_id", 1)).Limit(AccountingReportRules.MaximumCharts + 1).ToListAsync(ct);
        if (chartRows.Count != chartIds.Length || chartRows.Any(row => row["bookId"].AsInt64 != bookId)) throw AccountingReportRecords.Broken();
        if (chartRows.Sum(row => row["mappings"].AsBsonArray.Count) > AccountingReportRules.MaximumLines) throw AccountingReportRules.TooLarge();
        var charts = chartRows.ToDictionary(row => row["_id"].AsInt64, AccountingBson.ReadChart);
        var allIds = new BsonArray(journalMap.Keys.Order());
        var auditFilter = new BsonDocument("journalId", new BsonDocument("$in", allIds));
        await ValidRowsAsync("accountingAuditEvents", auditFilter, AccountingSchemas.Validator("accountingAuditEvents"), context.Tx, ct);
        var audits = await Rows("accountingAuditEvents").Find(context.Tx, auditFilter).Sort(new BsonDocument("_id", 1))
            .Limit(AccountingReportRules.MaximumJournals * 2 + 1).ToListAsync(ct);
        if (audits.Count != journalMap.Count) throw AccountingReportRecords.Broken();
        var auditMap = audits.GroupBy(row => row["journalId"].AsInt64).ToDictionary(group => group.Key, group => group.ToArray());
        var months = journalMap.Values.Select(row => row["month"].AsString).Distinct(StringComparer.Ordinal).ToArray();
        var periodFilter = new BsonDocument { { "bookId", bookId }, { "month", new BsonDocument("$in", new BsonArray(months)) } };
        await ValidRowsAsync("periods", periodFilter, AccountingSchemas.Validator("periods"), context.Tx, ct);
        var periods = await Rows("periods").Find(context.Tx, periodFilter).Limit(AccountingReportRules.MaximumJournals * 2 + 1).ToListAsync(ct);
        if (periods.Count != months.Length || periods.Select(row => row["month"].AsString).Distinct(StringComparer.Ordinal).Count() != months.Length) throw AccountingReportRecords.Broken();
        var drafts = new Dictionary<long, JournalDraft>();
        foreach (var row in journalMap.Values) {
            var id = row["_id"].AsInt64; var draft = AccountingBson.ReadDraft(row);
            if (row["bookId"].AsInt64 != bookId || draft.Book != context.Key || !charts.TryGetValue(draft.ChartId, out var chart) ||
                chart.Version != draft.ChartVersion || chart.BookId != bookId) throw AccountingReportRecords.Broken();
            foreach (var line in draft.Lines)
                if (!chart.Mappings.Any(mapping => mapping.MappingKey == line.MappingKey && mapping.AccountId == line.AccountId && mapping.AccountCode == line.AccountCode))
                    throw AccountingReportRecords.Broken();
            if (!auditMap.TryGetValue(id, out var linked) || linked.Length != 1) throw AccountingReportRecords.Broken();
            var audit = linked[0];
            if (audit["bookId"] != row["bookId"] || audit["eventKey"] != $"accounting:journal:{id}" ||
                audit["kind"] != (draft.ReversalOf.HasValue ? "journal.reversed" : "journal.posted") || audit["fingerprint"] != row["fingerprint"] ||
                new[] { "actorId", "actorGrantVersion", "reason", "correlationId" }.Any(field => audit[field] != row[field]) ||
                audit["createdAt"].ToUniversalTime() < row["createdAt"].ToUniversalTime()) throw AccountingReportRecords.Broken();
            drafts.Add(id, draft);
        }
        foreach (var (id, draft) in drafts) if (draft.ReversalOf is { } originalId) {
            if (originalId == id || !drafts.TryGetValue(originalId, out var original) || original.ReversalOf.HasValue ||
                original.Book != draft.Book || original.ChartId != draft.ChartId || original.ChartVersion != draft.ChartVersion ||
                original.Dimensions != draft.Dimensions || original.Lines.Length != draft.Lines.Length) throw AccountingReportRecords.Broken();
            for (var index = 0; index < draft.Lines.Length; index++) {
                var left = original.Lines[index]; var right = draft.Lines[index];
                if (left.MappingKey != right.MappingKey || left.AccountId != right.AccountId || left.AccountCode != right.AccountCode ||
                    left.Debit != right.Credit || left.Credit != right.Debit) throw AccountingReportRecords.Broken();
            }
        }
        var balances = new Dictionary<long, (SortedSet<string> Codes, Money Debit, Money Credit)>();
        foreach (var chart in charts.Values) foreach (var mapping in chart.Mappings) {
            if (!balances.TryGetValue(mapping.AccountId, out var balance)) {
                if (balances.Count >= AccountingReportRules.MaximumAccounts) throw AccountingReportRules.TooLarge();
                balance = (new(StringComparer.Ordinal), new(0m), new(0m));
            }
            balance.Codes.Add(mapping.AccountCode); balances[mapping.AccountId] = balance;
        }
        Money totalDebit = new(0m), totalCredit = new(0m); var journals = new List<AccountingReportJournal>();
        string? previousDate = null; long previousId = 0;
        foreach (var row in selected) {
            var id = row["_id"].AsInt64; var draft = drafts[id]; var date = draft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date != row["businessDate"].AsString || string.CompareOrdinal(date, endDate) > 0 ||
                previousDate is not null && (string.CompareOrdinal(previousDate, date) > 0 || previousDate == date && previousId >= id)) throw AccountingReportRecords.Broken();
            previousDate = date; previousId = id;
            foreach (var line in draft.Lines) {
                var balance = balances[line.AccountId];
                balance.Debit = Add(balance.Debit, line.Debit); balance.Credit = Add(balance.Credit, line.Credit); balances[line.AccountId] = balance;
                totalDebit = Add(totalDebit, line.Debit); totalCredit = Add(totalCredit, line.Credit);
            }
            journals.Add(new(new(id), date, new(draft.Source.Namespace, new(draft.Source.DocumentId), draft.Source.EventKind, new(draft.Source.Occurrence)),
                new(draft.ChartId), new(draft.ChartVersion), draft.ReversalOf is { } original ? new LongId(original) : null, row["reason"].AsString,
                Array.AsReadOnly(draft.Lines.Select(line => new AccountingReportLine(line.MappingKey, new(line.AccountId), line.AccountCode, line.Debit, line.Credit)).ToArray())));
        }
        if (totalDebit != totalCredit) throw AccountingReportRecords.Broken();
        var accounts = Array.AsReadOnly(balances.OrderBy(pair => pair.Key).Select(pair => new AccountingReportAccount(new(pair.Key),
            Array.AsReadOnly(pair.Value.Codes.ToArray()), pair.Value.Debit, pair.Value.Credit, new(pair.Value.Debit.Value - pair.Value.Credit.Value))).ToArray());
        var selection = new BsonArray(selected.Select(row => new BsonDocument { { "journalId", row["_id"] }, { "contentHash", AccountingReportRecords.Hash(row) } }));
        var bookHash = AccountingReportRecords.Hash(AccountingReportRecords.StableBook(context.Book));
        var chartHash = AccountingReportRecords.Hash(new BsonArray(chartRows));
        var hash = AccountingReportRecords.Hash(new BsonDocument { { "kind", "accounting-report-snapshot-v1" },
            { "key", AccountingBson.Key(context.Key) }, { "endDate", endDate }, { "bookHash", bookHash },
            { "sourcesHash", AccountingReportRecords.Hash(context.Sources) }, { "chartSetHash", chartHash }, { "selection", selection },
            { "provenance", new BsonArray(journalMap.OrderBy(pair => pair.Key).Select(pair => pair.Value)) }, { "audits", new BsonArray(audits) } });
        Current(context); ct.ThrowIfCancellationRequested(); return new(Book(context), endDate, hash, bookHash, chartHash, selection,
            totalDebit, totalCredit, accounts, journals.AsReadOnly());
    }
    private static Money Add(Money left, Money right)
    {
        try { return Money.Add(left, right); }
        catch (DomainValidationException) { throw AccountingReportRules.TooLarge(); }
    }
}

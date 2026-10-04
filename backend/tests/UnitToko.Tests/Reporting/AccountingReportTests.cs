using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Reporting;

namespace UnitToko.Tests.Reporting;

[Collection("Mongo")]
public sealed class AccountingReportTests(MongoFixture fixture)
{
    [Fact]
    public async Task Report_only_reader_gets_exact_persisted_cumulative_balances_and_original_csv_after_new_posts_and_service_restart()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "journalEntries"), Builders<BsonDocument>.Update
            .Set("value", 9_007_199_254_740_992L), new UpdateOptions { IsUpsert = true });
        var original = await data.PostAsync();
        var state = await data.FinancialStateAsync();
        var books = await data.Service.BooksAsync(data.Reader, AccountingReportTestData.Metadata);
        var book = Assert.Single(books.Items); Assert.Equal("IDR", book.Key.Currency);
        var report = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        Assert.Equal(original.Id, Assert.Single(report.Journals).Id.Value); Assert.Equal(100m, report.Debit.Value); Assert.Equal(report.Debit, report.Credit);
        Assert.False(report.IsApprovedClose); Assert.True(report.Cumulative); Assert.Equal("Asia/Jakarta", report.TimeZone);
        Assert.Equal(-100m, Assert.Single(report.Accounts, account => account.AccountCodes.Contains("LAP")).Balance.Value);
        Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
        var firstExport = await data.Service.ExportAsync(data.Reader, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata);
        Assert.Equal(report.ReadId, firstExport.SnapshotReadId); Assert.Equal(report.SnapshotHash, firstExport.SnapshotHash);
        Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
        await data.PostAsync(43, amount: 75.25m);
        var afterPosting = await data.FinancialStateAsync();
        var repeatedExport = await data.Service.ExportAsync(data.Reader, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata);
        Assert.Equal(firstExport.Bytes, repeatedExport.Bytes); Assert.NotEqual(firstExport.ReadId, repeatedExport.ReadId);
        Assert.Equal(afterPosting.ToBson(), (await data.FinancialStateAsync()).ToBson());
        var refreshed = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        Assert.Equal(2, refreshed.Journals.Count); Assert.Equal(175.25m, refreshed.Debit.Value);
        Assert.NotEqual(report.SnapshotHash, refreshed.SnapshotHash);
        var events = await data.Rows(AccountingReportRecords.Events).Find(new BsonDocument()).ToListAsync();
        Assert.Equal(5, events.Count);
        Assert.All(events, row => { Assert.Equal("report.read", row["permission"]); Assert.Equal("accounting-reporting", row["purpose"]); Assert.Equal(AccountingReportRecords.EventHash(row), row["eventHash"]); });
        var wire = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"balance\":\"-100.00\"", wire, StringComparison.Ordinal); Assert.Contains("\"debit\":\"100.00\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"9007199254740993\"", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_shop_grant_cannot_be_inferred_from_buying_warehouse_book_setup_or_other_actor_snapshot()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture); await data.PostAsync();
        foreach (var actor in new[] { data.BuyerReader, data.WarehouseReader, data.Setup.Reader, data.ForeignReader,
            data.Reader with { Assurance = FactorAssurance.SingleFactor } }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(actor, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.BooksAsync(data.BuyerReader, AccountingReportTestData.Metadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.BooksAsync(data.WarehouseReader, AccountingReportTestData.Metadata));
        Assert.Empty((await data.Service.BooksAsync(data.ForeignReader, AccountingReportTestData.Metadata)).Items);
        var report = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        var sameShop = await data.Base.AddActorAsync(606, UnitToko.Tests.Accounting.AccountingBookSetupTestData.Scope, Permissions.ReportRead);
        var unavailable = await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ExportAsync(sameShop, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata));
        Assert.Equal("accounting_report_snapshot_unavailable", unavailable.Code);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Reader.UserId), Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", data.Clock.UtcNow.UtcDateTime));
        var count = await data.Rows(AccountingReportRecords.Events).CountDocumentsAsync(new BsonDocument());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ExportAsync(data.Reader, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata));
        Assert.Equal(count, await data.Rows(AccountingReportRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Inclusive_calendar_cutoff_keeps_prior_rows_and_stored_reversal_lines_without_reposting()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        var original = await data.PostAsync(); await data.ReverseAsync(44, original.Id);
        var state = await data.FinancialStateAsync();
        var report = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        Assert.Equal(2, report.Journals.Count); Assert.Equal(original.Id, report.Journals[1].ReversalOf!.Value.Value);
        Assert.Equal(200m, report.Debit.Value); Assert.Equal(report.Debit, report.Credit);
        Assert.All(report.Accounts, row => Assert.Equal(0m, row.Balance.Value));
        Assert.Equal(report.Journals[0].Lines[0].Debit, report.Journals[1].Lines[0].Credit);
        var yesterday = AccountingReportRules.EndDate(data.EndDate).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var empty = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, yesterday, AccountingReportTestData.Metadata);
        Assert.Empty(empty.Journals); Assert.NotEmpty(empty.Accounts); Assert.Equal(0m, empty.Debit.Value);
        var emptyCsv = await data.Service.ExportAsync(data.Reader, empty.ReadId.Value, empty.SnapshotHash, AccountingReportTestData.Metadata);
        Assert.NotEmpty(emptyCsv.Bytes); Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
        Assert.Equal(new DateOnly(2026, 10, 1), AccountingCalendar.BusinessDate(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Required_audit_failure_and_corrupted_frozen_sources_release_no_report_and_do_not_change_financial_owners()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture); await data.PostAsync();
        var state = await data.FinancialStateAsync();
        var stronger = new BsonDocument("$and", new BsonArray { UnitToko.Platform.Mongo.Migrations.AccountingReportSchemas.Validator(),
            new BsonDocument("operation", new BsonDocument("$ne", AccountingReportRecords.ReadOperation)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountingReportRecords.Events }, { "validator", stronger } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata));
        Assert.Empty(await data.Rows(AccountingReportRecords.Events).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountingReportRecords.Events },
            { "validator", UnitToko.Platform.Mongo.Migrations.AccountingReportSchemas.Validator() } });
        var report = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument()).SingleAsync();
        var originalCode = chart["mappings"][0]["accountCode"];
        await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", chart["_id"]), Builders<BsonDocument>.Update.Set("mappings.0.accountCode", "CHANGED"));
        var broken = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ExportAsync(data.Reader, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata));
        Assert.Equal("accounting_report_integrity_unavailable", broken.Code);
        await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", chart["_id"]), Builders<BsonDocument>.Update.Set("mappings.0.accountCode", originalCode));
        await data.Rows(AccountingReportRecords.Events).UpdateOneAsync(new BsonDocument("_id", report.ReadId.Value), Builders<BsonDocument>.Update.Set("correlationId", "tampered-read"));
        Assert.Equal("accounting_report_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ExportAsync(data.Reader,
            report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata))).Code);
        Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
    }

    [Fact]
    public async Task Expired_membership_and_export_deadline_are_rechecked_before_releasing_persisted_snapshot()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture); await data.PostAsync();
        var report = await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata);
        data.Clock.UtcNow = report.ExpiresAt;
        var stale = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ExportAsync(data.Reader, report.ReadId.Value, report.SnapshotHash, AccountingReportTestData.Metadata));
        Assert.Equal("accounting_report_snapshot_stale", stale.Code);
        await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", data.Reader.UserId), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime));
        var before = await data.Rows(AccountingReportRecords.Events).CountDocumentsAsync(new BsonDocument());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata));
        Assert.Equal(before, await data.Rows(AccountingReportRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Oversized_cumulative_selection_and_money_totals_fail_instead_of_returning_partial_rows_or_wrapped_balances()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        await data.PostAsync(amount: DomainValues.MaximumMoney); await data.PostAsync(43, amount: 1m);
        Assert.Equal("accounting_report_too_large", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ReadAsync(data.Reader,
            AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata))).Code);
        Assert.Empty(await data.Rows(AccountingReportRecords.Events).Find(new BsonDocument()).ToListAsync());
        var retained = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).FirstAsync();
        var original = UnitToko.Platform.Accounting.AccountingBson.ReadDraft(retained);
        // Adversarial retained-data fixture: even an oversized canonical selection is never quietly truncated.
        var rows = Enumerable.Range(1, AccountingReportRules.MaximumJournals).Select(index => {
            var draft = JournalDraft.FromResolved(original.Source with { DocumentId = 90_000 + index }, original.SourceRevision,
                original.Book, original.Dimensions, original.Date, original.ChartId, original.ChartVersion, original.Lines, null);
            return UnitToko.Platform.Accounting.AccountingBson.Journal(90_000 + index, retained["bookId"].AsInt64, draft, data.PostingActor,
                new("Synthetic oversized retained-data boundary fixture", "synthetic-report-limit"), data.Clock.UtcNow);
        }).ToArray();
        await data.Rows("journalEntries").InsertManyAsync(rows);
        var state = await data.FinancialStateAsync();
        Assert.Equal("accounting_report_too_large", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ReadAsync(data.Reader,
            AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata))).Code);
        Assert.Empty(await data.Rows(AccountingReportRecords.Events).Find(new BsonDocument()).ToListAsync());
        Assert.Equal(state.ToBson(), (await data.FinancialStateAsync()).ToBson());
    }
}

public sealed class AccountingReportContractTests
{
    [Fact]
    public void Canonical_calendar_and_purpose_metadata_reject_impossible_dates_aliases_controls_and_unpaired_unicode()
    {
        Assert.Equal(new DateOnly(2024, 2, 29), AccountingReportRules.EndDate("2024-02-29"));
        foreach (var invalid in new[] { "2026-02-29", "2026-09-31", "0000-01-01", "0001-01-01", "2026-9-28", "2026-09-28 ", "2026-09-28T00:00:00Z" })
            Assert.Throws<DomainValidationException>(() => AccountingReportRules.EndDate(invalid));
        foreach (var invalid in new[] { " test", "test ", "test\nref", "test\u200bref", "test\ud800ref", "e\u0301", new string('x', 129) })
            Assert.Throws<DomainValidationException>(() => new AccountingReportReadMetadata(AccountingReportReadMetadata.ReportingPurpose, invalid).Validate());
        Assert.Throws<DomainValidationException>(() => new AccountingReportReadMetadata("reporting", "valid-reference").Validate());
        new AccountingReportReadMetadata(AccountingReportReadMetadata.ReportingPurpose, "permintaan-é-123").Validate();
    }

    [Fact]
    public void Csv_has_bom_crlf_lossless_ids_quoted_unicode_and_neutralized_formula_text_but_numeric_negative_money()
    {
        const long large = 9_007_199_254_740_993L;
        var now = DateTimeOffset.Parse("2026-10-04T00:00:00Z", CultureInfo.InvariantCulture);
        var book = new AccountingReportBook(new(large), new(new(100), new(101), new(103), "IDR"),
            new(new(100), "ORG", "Nama \"koperasi\", Jakarta"), new(new(101), "SHOP", "Toko"), new(new(103), "RETAIL", "Ritel"));
        var snapshot = new AccountingReportSnapshot(new(1), new string('a', 64), book, "2026-10-04", now, now.AddMinutes(15), new(10m), new(10m),
            [new(new(large), ["AP"], new(0m), new(10m), new(-10m))],
            [new(new(large), "2026-10-04", new("fixture", new(large), "receipt", new(1)), new(1), new(1), null,
                "  =HYPERLINK(\"https://example.invalid\")", [new("payable", new(large), "AP", new(0m), new(10m))])]);
        var bytes = AccountingReportCsvWriter.Create(snapshot);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
        var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan(3));
        Assert.Contains("\"'9007199254740993\"", text, StringComparison.Ordinal);
        Assert.Contains("\"'  =HYPERLINK(\"\"https://example.invalid\"\")\"", text, StringComparison.Ordinal);
        Assert.Contains("Nama \"\"koperasi\"\", Jakarta", text, StringComparison.Ordinal);
        Assert.Contains(",0.00,10.00,-10.00\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        foreach (var danger in new[] { "=SUM(A1)", "+cmd", "-cmd", "@SUM(A1)", "  =1", "\tcmd", " \t +cmd", "\rcmd", "\ncmd", "\ufeff=1", "\u200b@SUM(A1)" })
            Assert.StartsWith("\"'", AccountingReportCsvWriter.Text(danger), StringComparison.Ordinal);
        Assert.Equal("\"normal text\"", AccountingReportCsvWriter.Text("normal text"));
        var huge = snapshot with { Journals = [snapshot.Journals[0] with { Description = new string('x', AccountingReportRules.MaximumCsvBytes) }] };
        Assert.Equal("accounting_report_too_large", Assert.Throws<DomainValidationException>(() => AccountingReportCsvWriter.Create(huge)).Code);
    }
}

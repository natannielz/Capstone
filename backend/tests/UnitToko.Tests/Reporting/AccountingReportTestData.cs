using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Reporting;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Reporting;

// Synthetic integration fixture through actual approved profile/book producers and the authorized posting writer.
internal sealed class AccountingReportTestData(AccountingBookSetupTestData setup) : IAsyncDisposable
{
    internal AccountingBookSetupTestData Setup { get; } = setup;
    internal StoreTestData Base => Setup.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Setup.Unit;
    internal ActorContext Reader = null!, ForeignReader = null!, WarehouseReader = null!, BuyerReader = null!, PostingActor = null!;
    internal AccountingReportService Service => new(Database, Unit, new(Database), Clock);
    internal static readonly OperationalBookKey Key = AccountingBookSetupTestData.Key;
    internal static readonly AccountingReportReadMetadata Metadata = new(AccountingReportReadMetadata.ReportingPurpose, "synthetic-report-validation");
    internal string EndDate => AccountingCalendar.BusinessDate(Clock.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static async Task<AccountingReportTestData> CreateAsync(MongoFixture fixture, int? migrationTarget = null)
    {
        var setup = await AccountingBookSetupTestData.CreateAsync(fixture, migrationTarget);
        try {
            var data = new AccountingReportTestData(setup);
            await AccountingReportTestSchema.ApplyAsync(data.Database, data.Clock);
            var input = setup.Input() with { InitialMonth = AccountingCalendar.Month(AccountingCalendar.BusinessDate(data.Clock.UtcNow)) };
            var proposal = await setup.ProposeAsync("report-book-proposed", input); await setup.DecideAsync(proposal, "report-book-approved");
            data.Reader = await data.Base.AddActorAsync(601, AccountingBookSetupTestData.Scope, Permissions.ReportRead);
            data.ForeignReader = await data.Base.AddActorAsync(602, new(GrantScopeKind.FulfillmentShop, 100, ShopId: setup.ForeignShopId, ChannelId: 103), Permissions.ReportRead);
            data.WarehouseReader = await data.Base.AddActorAsync(603, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103), Permissions.ReportRead);
            data.BuyerReader = await data.Base.AddActorAsync(604, new(GrantScopeKind.BuyerOwned, 10, BranchId: 11), Permissions.ReportRead);
            data.PostingActor = await data.Base.AddActorAsync(605, new(GrantScopeKind.BuyerOwned, 10, BranchId: 11), Permissions.OrderSubmit);
            return data;
        } catch { await setup.DisposeAsync(); throw; }
    }
    internal async Task<JournalReceipt> PostAsync(long documentId = 42, string? date = null, decimal amount = 100m,
        string description = "Synthetic persisted goods receipt")
    {
        date ??= EndDate;
        await Base.AddResourceAsync(documentId, 10, 11, PostingActor.UserId);
        await Rows("fixtureBusinessRecords").UpdateOneAsync(new BsonDocument("_id", documentId), Builders<BsonDocument>.Update
            .Set("amount", new BsonDecimal128(amount)).Set("date", date).Set("mapping", "inventory"));
        var source = new JournalSource("fixture", documentId, "receipt", 1);
        var writer = new PostJournal(Database, Clock, [new FixtureSourceReader(Database)]);
        var metadata = new AuthorizationCommandMetadata("report-fixture-post-" + documentId, description, "synthetic-report-posting");
        var result = await Base.Store.ExecuteAuthorizedAsync(PostingActor, "fixture.report.post", Permissions.OrderSubmit, metadata, new { source },
            async (tx, token) => new(AuthorizationBson.Resource(await Rows("fixtureBusinessRecords").Find(tx, new BsonDocument("_id", documentId)).SingleAsync(token))),
            async (tx, token) => {
                var receipt = await writer.PostAsync(tx, PostingActor, source, 1, new(description, metadata.CorrelationId), token);
                return new BsonDocument { { "journalId", receipt.Id }, { "sourceEvent", receipt.SourceEvent }, { "fingerprint", receipt.Fingerprint } };
            });
        return new(result["journalId"].AsInt64, result["sourceEvent"].AsString, result["fingerprint"].AsString);
    }
    internal async Task<JournalReceipt> ReverseAsync(long documentId, long originalId, string? date = null)
    {
        date ??= EndDate; await Base.AddResourceAsync(documentId, 10, 11, PostingActor.UserId);
        await Rows("fixtureBusinessRecords").UpdateOneAsync(new BsonDocument("_id", documentId), Builders<BsonDocument>.Update
            .Set("date", date).Set("originalJournalId", originalId));
        var source = new JournalSource("fixture", documentId, "reversal", 1);
        var writer = new PostJournal(Database, Clock, [new FixtureSourceReader(Database)]);
        var result = await Base.Store.ExecuteAuthorizedAsync(PostingActor, "fixture.report.reverse", Permissions.OrderSubmit,
            new("report-fixture-reverse-" + documentId, "Synthetic full reversal", "synthetic-report-reversal"), new { source },
            async (tx, token) => new(AuthorizationBson.Resource(await Rows("fixtureBusinessRecords").Find(tx, new BsonDocument("_id", documentId)).SingleAsync(token))),
            async (tx, token) => {
                var receipt = await writer.ReverseAsync(tx, PostingActor, source, 1, new("Synthetic full reversal", "synthetic-report-reversal"), token);
                return new BsonDocument { { "journalId", receipt.Id }, { "sourceEvent", receipt.SourceEvent }, { "fingerprint", receipt.Fingerprint } };
            });
        return new(result["journalId"].AsInt64, result["sourceEvent"].AsString, result["fingerprint"].AsString);
    }
    internal async Task<BsonDocument> FinancialStateAsync()
    {
        var snapshot = new BsonDocument();
        foreach (var name in new[] { "accountingBooks", "accountingChartVersions", "journalEntries", "periods", "accountingPeriodHistory", "accountingAuditEvents",
            "orders", "invoices", "stockBalances", "reservations", "movements", "idempotencyRecords", "auditEvents", "outboxEvents" }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); snapshot[name] = new BsonArray(rows);
        }
        return snapshot;
    }
    public ValueTask DisposeAsync() => Setup.DisposeAsync();
}

internal static class AccountingReportTestSchema
{
    internal static async Task ApplyAsync(IMongoDatabase database, UnitToko.Platform.IClock clock)
    {
        var gate = new MigrationGate(database, clock, CollectionMigrations.CurrentVersion);
        var lease = await gate.AcquireAsync(84, default); if (lease is null) return;
        try { await new AccountingReportMigration(database).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
}

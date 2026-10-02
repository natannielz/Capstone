using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Organizations;

namespace UnitToko.Tests.Accounting;

// Actual approved organization profiles and book producers; no seeded book/chart/period.
internal sealed class AccountingBookSetupTestData(OrganizationAddressTestData organizations) : IAsyncDisposable
{
    internal const string Proposals = "accountingBookSetupProposals", Decisions = "accountingBookSetupDecisions", Prefix = "accounting.book.";
    internal const string Books = "accountingBooks", Charts = "accountingChartVersions", Periods = "periods";
    internal OrganizationAddressTestData Organizations { get; } = organizations;
    internal StoreTestData Base => Organizations.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Organizations.Unit;
    internal AccountingBookSetupService Service => new(Database, Unit, new(Database), Clock);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!, Warehouse = null!;
    internal long ForeignShopId;
    internal static readonly OperationalBookKey Key = new(100, 101, 103, "IDR");
    internal static readonly GrantScope Scope = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    internal AccountingBookSetupInput Input() => new(Key, Clock.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        [new("inventory", "ASTOCK"), new("payable", "LAP"), new("supplier_debt", "LAP")]);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed initial accounting configuration", "accounting-book-test");
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static async Task<AccountingBookSetupTestData> CreateAsync(MongoFixture fixture) {
        var source = await StoreTestData.CreateAsync(fixture);
        try {
            source.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var data = new AccountingBookSetupTestData(await OrganizationAddressTestData.PrepareAsync(source, fixture.Client));
            var channel = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Channel, 100, "RETAIL", "Synthetic retail channel", 103, 1, 0), Meta("book-channel"));
            await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, channel.Id, 1, Meta("book-channel-approved"));
            var foreign = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                new(OrganizationChangeKind.Create, ScopeNodeKind.Shop, 100, "OTHER", "Synthetic other shop"), Meta("book-other-shop"));
            data.ForeignShopId = (await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, foreign.Id, 1, Meta("book-other-shop-approved"))).ScopeId!.Value;
            data.Maker = await source.AddActorAsync(510, Scope, Permissions.AccountingBookRead, Permissions.AccountingBookPropose);
            data.Checker = await source.AddActorAsync(511, Scope, Permissions.AccountingBookRead, Permissions.AccountingBookApprove);
            data.Reader = await source.AddActorAsync(512, Scope, Permissions.AccountingBookRead);
            data.Outsider = await source.AddActorAsync(513, new(GrantScopeKind.FulfillmentShop, 100, ShopId: data.ForeignShopId, ChannelId: 103),
                Permissions.AccountingBookRead, Permissions.AccountingBookPropose, Permissions.AccountingBookApprove);
            data.Warehouse = await source.AddActorAsync(514, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103),
                Permissions.AccountingBookRead, Permissions.AccountingBookPropose, Permissions.AccountingBookApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock,
                data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId, data.Warehouse.UserId);
            return data;
        } catch { await source.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key, AccountingBookSetupInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposal, string key, AccountingBookSetupDecision decision = AccountingBookSetupDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, decision);
        return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument();
        foreach (var table in new[] { Proposals, Decisions, Books, Charts, Periods }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^accounting\.book(?:[.:]|$)")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task NoFinancialEffectsAsync() {
        foreach (var table in new[] { "journalEntries", "accountingPeriodHistory", "accountingAuditEvents", "orders", "invoices", "stockBalances", "reservations", "movements", "creditVersions" })
            Assert.False(await Rows(table).Find(new BsonDocument()).AnyAsync());
    }
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}

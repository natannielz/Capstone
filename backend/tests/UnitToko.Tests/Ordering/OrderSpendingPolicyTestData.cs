using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Organizations;

namespace UnitToko.Tests.Ordering;

// Actual organization and spending producers; synthetic reviewed people/assignments only.
internal sealed class OrderSpendingPolicyTestData(OrganizationAddressTestData organizations) : IAsyncDisposable
{
    internal const string Heads = "orderSpendingPolicyHeads", Proposals = "orderSpendingPolicyProposals",
        Decisions = "orderSpendingPolicyDecisions", Versions = "orderSpendingPolicyVersions", Prefix = "ordering.spending.policy.";
    internal const string Read = "order.spending.policy.read", Propose = "order.spending.policy.propose", Approve = "order.spending.policy.approve";
    internal OrganizationAddressTestData Organizations { get; } = organizations;
    internal StoreTestData Base => Organizations.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Organizations.Unit;
    internal OrderSpendingPolicyService Service => new(Database, Unit, new(Database), Clock, Organizations.Organizations);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!, Orderer = null!;
    internal static readonly OrderSpendingPolicyTarget Branch = new(ScopeNodeKind.Branch, 11);
    internal static readonly GrantScope Scope = new(GrantScopeKind.BuyingBranch, 10, 11);
    internal OrderSpendingPolicyInput Input(long version = 0) => new(Branch, 2, 1, version,
        new(OrderSpendingMode.AboveThreshold, Money.Parse("500000.25")), Clock.UtcNow.AddMinutes(-1), Clock.UtcNow.AddDays(30));
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed spending rule", "spending-policy-test");
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static async Task<OrderSpendingPolicyTestData> CreateAsync(MongoFixture fixture) {
        var source = await StoreTestData.CreateAsync(fixture);
        try {
            source.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var data = new OrderSpendingPolicyTestData(await OrganizationAddressTestData.PrepareAsync(source, fixture.Client));
            data.Maker = await source.AddActorAsync(410, Scope, Propose, Read, Permissions.BranchPic);
            data.Checker = await source.AddActorAsync(411, Scope, Approve, Read, Permissions.BranchPic);
            data.Reader = await source.AddActorAsync(412, Scope, Read, Permissions.BranchPic);
            data.Outsider = await source.AddActorAsync(413, new(GrantScopeKind.BuyingBranch, 10, 12), Read, Propose, Approve, Permissions.BranchPic);
            data.Orderer = await source.AddActorAsync(414, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock,
                data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId, data.Orderer.UserId);
            return data;
        } catch { await source.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key, OrderSpendingPolicyInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview.Binding, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposal, string key, OrderSpendingPolicyDecision decision = OrderSpendingPolicyDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, 1, decision);
        return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, decision, preview.Binding, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument();
        foreach (var table in new[] { Heads, Proposals, Decisions, Versions }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^ordering\.spending\.policy(?:[.:]|$)")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}

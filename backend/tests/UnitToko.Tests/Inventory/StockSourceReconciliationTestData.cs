using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Inventory;

// Real approved source/policy, private video inspection and held receipt; synthetic local people only.
[SupportedOSPlatform("windows")]
internal sealed class StockSourceReconciliationTestData(ProcurementStockReceiptTestData stock) : IAsyncDisposable
{
    internal const string ReadPermission = "stock.source.reconciliation.read", ProposePermission = "stock.source.reconciliation.propose",
        DecidePermission = "stock.source.reconciliation.decide";
    internal const string Reason = "Synthetic complete physical observation; no stock correction, release, valuation or payment";
    internal ProcurementStockReceiptTestData Stock { get; } = stock;
    internal StoreTestData Base => Stock.Base;
    internal IMongoDatabase Database => Stock.Database;
    internal MongoUnitOfWork Unit => Stock.Unit;
    internal MutableAuthorizationClock Clock => Stock.Clock;
    internal StockSourceSetupService Sources => Stock.Sources;
    internal StockSourceSetupService Service => Sources;
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!;
    private readonly Dictionary<long, string> bindings = [];
    internal BsonDocument Receipt = null!, Balance = null!;
    internal long PoolId => Stock.PoolId;
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "synthetic-source-reconciliation-test");
    internal async Task<StockSourceReconciliationInput> InputAsync(string reference = "SYNTHETIC-PHYSICAL-OBSERVATION", long? packages = null)
    {
        var baseline = await Service.ReadReconciliationBaselineAsync(Maker, PoolId);
        bindings[baseline["poolFence"].AsInt64] = baseline["binding"].AsString;
        return new(PoolId, baseline["poolRevision"].AsInt64, baseline["poolFence"].AsInt64, baseline["policyId"].AsInt64,
            baseline["policyRevision"].AsInt64, baseline["configurationHash"].AsString,
            new(Clock.UtcNow, reference, baseline["balances"].AsBsonArray.Select(row =>
                new StockSourceObservedBalance(row["balanceId"].AsInt64, packages ?? row["onHand"].AsInt64)).ToImmutableArray()));
    }
    internal StockSourceReconciliationObservation Review(StockSourceReconciliationInput input, string reference = "SYNTHETIC-INDEPENDENT-COUNT") =>
        new(Clock.UtcNow, reference, input.Observation.Entries.Select(row => new StockSourceObservedBalance(row.BalanceId, row.Packages)).ToImmutableArray());
    internal string Binding(StockSourceReconciliationInput input) => bindings[input.PoolFence];
    internal async Task<BsonDocument> ProposeAsync(string key, StockSourceReconciliationInput? input = null)
    {
        input ??= await InputAsync(); return await Service.ProposeReconciliationAsync(Maker, input, Binding(input), Meta(key));
    }
    internal async Task<BsonDocument> ConfirmAsync(long proposalId, StockSourceReconciliationInput input, string key)
    {
        var observed = Review(input);
        var preview = await Service.PreviewReconciliationDecisionAsync(Checker, proposalId, StockSourceReconciliationDecision.Confirm, observed);
        return await Service.DecideReconciliationAsync(Checker, proposalId, 1, StockSourceReconciliationDecision.Confirm, observed, preview["binding"].AsString, Meta(key));
    }

    internal static async Task<StockSourceReconciliationTestData> CreateAsync(MongoFixture fixture, int packages = 2, bool browserObservationWindow = false)
    {
        var stock = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var data = new StockSourceReconciliationTestData(stock);
        try {
            var scope = ProcurementRequestTestData.Scope;
            data.Maker = await data.Base.AddActorAsync(740, scope, Permissions.StockRead, Permissions.StockSourceRead, ReadPermission, ProposePermission);
            data.Checker = await data.Base.AddActorAsync(741, scope, Permissions.StockRead, Permissions.StockSourceRead, ReadPermission, DecidePermission);
            data.Reader = await data.Base.AddActorAsync(742, scope, Permissions.StockRead, Permissions.StockSourceRead, ReadPermission);
            data.Outsider = await data.Base.AddActorAsync(743, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: stock.Inspection.Receiving.Procurement.ForeignShopId, WarehouseId: stock.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103),
                Permissions.StockRead, Permissions.StockSourceRead, ReadPermission, ProposePermission, DecidePermission);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            if (browserObservationWindow) await data.ApproveBrowserObservationWindowAsync();
            var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
            data.Receipt = await stock.RecordAsync("reconciliation-held-source", stock.Input("SYNTHETIC-RECONCILIATION-RECEIPT", packages) with { PoolRevision = pool["revision"].AsInt64 });
            data.Balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.Receipt["balanceId"])).SingleAsync();
            Assert.Equal((long)packages, data.Balance["onHand"].AsInt64);
            Assert.Equal((long)packages, data.Balance["held"].AsInt64);
            Assert.Equal(0L, data.Balance["reserved"].AsInt64);
            Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync())["active"].AsBoolean);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }

    internal async Task<BsonDocument> ProtectedAsync()
    {
        var result = new BsonDocument { { "held", await Stock.StateAsync() }, { "inspection", await Stock.Inspection.StateAsync() } };
        foreach (var table in new[] { "journalEntries", "accountingAuditEvents", "cashMovements", "paymentAllocations", "reservations",
            "procurementStockValuationProposals", "procurementStockValuationDecisions", "procurementStockValuationAllocations" })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }

    internal async Task<BsonDocument> StateAsync()
    {
        var result = new BsonDocument();
        foreach (var table in new[] { StockSourceReconciliationBson.Proposals, StockSourceReconciliationBson.Decisions })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var (table, field) in new[] { ("auditEvents", "operation"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^stock\.source\.reconciliation\.")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }

    internal async Task<StockLocation> AddReviewedChannelAsync()
    {
        Clock.UtcNow = Clock.UtcNow.AddSeconds(1);
        var organization = Stock.Inspection.Receiving.Procurement.Organizations;
        var proposed = await organization.Organizations.ProposeAsync(organization.ProfileMaker,
            new(OrganizationChangeKind.Create, ScopeNodeKind.Channel, 100, "RECON-LINK", "Synthetic linked reconciliation channel"), Meta("reconciliation-channel"));
        var approved = await organization.Organizations.ApproveAsync(organization.ProfileChecker, proposed.Id, 1, Meta("reconciliation-channel-approved"));
        var linked = new StockLocation(new(100), new(101), new(102), new(approved.ScopeId!.Value));
        await Base.AddGrantAsync(Stock.SourceMaker, 7440, linked.ToScope(),
            [Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose]);
        await Base.AddGrantAsync(Stock.SourceChecker, 7441, linked.ToScope(),
            [Permissions.StockSourceRead, Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove]);
        var alias = await Sources.ProposeAliasAsync(Stock.SourceMaker, PoolId, 1, linked, 0, true, Meta("reconciliation-alias"));
        await Sources.DecideAliasAsync(Stock.SourceChecker, alias["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic independently approved linked channel", Meta("reconciliation-alias-approved"));
        var preview = await Sources.PreviewOperatingPolicyAsync(Stock.SourceMaker, PoolId);
        var expected = new StockSourcePolicyExpectation(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64,
            preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
        var policy = await Sources.ProposeOperatingPolicyAsync(Stock.SourceMaker, PoolId, expected,
            new(StockMovementWriter.UnitToko, null, 0, "Synthetic complete linked-channel observation", "Synthetic stopped-pool outage procedure"), Meta("reconciliation-linked-policy"));
        await Sources.DecideOperatingPolicyAsync(Stock.SourceChecker, policy["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic independent complete-channel policy review", Meta("reconciliation-linked-policy-approved"));
        return linked;
    }

    private async Task ApproveBrowserObservationWindowAsync()
    {
        // Explicit synthetic finite acceptance for the full browser journey, through the real maker/checker owner.
        // Ordinary tests retain the original 60001ms source age and the application has no changed default.
        Clock.UtcNow = Clock.UtcNow.AddSeconds(1);
        var original = await Rows("stockSourceProposals").Find(new BsonDocument()).SingleAsync();
        var input = StockSourceSetupBson.ReadInput(original["input"].AsBsonDocument) with { MaximumAgeMilliseconds = 900000 };
        var proposed = await Sources.ProposeRevisionAsync(Stock.SourceMaker, PoolId, 1, input, Meta("browser-observation-window"));
        await Sources.DecideRevisionAsync(Stock.SourceChecker, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Explicit synthetic fifteen-minute complete-observation acceptance", Meta("browser-observation-window-approved"));
        var preview = await Sources.PreviewOperatingPolicyAsync(Stock.SourceMaker, PoolId);
        var expected = new StockSourcePolicyExpectation(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64,
            preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
        proposed = await Sources.ProposeOperatingPolicyAsync(Stock.SourceMaker, PoolId, expected,
            new(StockMovementWriter.UnitToko, null, 0, "Synthetic complete browser observation", "Synthetic stopped-pool outage procedure"), Meta("browser-observation-policy"));
        await Sources.DecideOperatingPolicyAsync(Stock.SourceChecker, proposed["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Synthetic independent observation procedure review", Meta("browser-observation-policy-approved"));
    }

    public ValueTask DisposeAsync() => Stock.DisposeAsync();
}

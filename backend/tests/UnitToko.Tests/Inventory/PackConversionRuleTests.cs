using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Tests.Inventory;

// Genuine catalog and scoped rule commands, synthetic local accounts/people.
// An approved rule is preparation: no active source, cost layer or physical conversion is seeded.
[Collection("Mongo")]
public sealed class PackConversionRuleTests(MongoFixture fixture)
{
    [Fact]
    public async Task Exact_current_packages_scoped_pending_discovery_independent_review_restart_and_original_keys_preserve_all_stock_and_money()
    {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.PhysicalAsync();
        var source = Assert.Single((await data.Service.PackageChoicesAsync(data.Maker, Data.Location, PackConversionChoiceRole.Source, 0, 25, null,
            new("pack-conversion-rule-source", "synthetic-rule-source"))).Items);
        var destination = Assert.Single((await data.Service.PackageChoicesAsync(data.Maker, Data.Location, PackConversionChoiceRole.Destination, 0, 25, null,
            new("pack-conversion-rule-destination", "synthetic-rule-destination"))).Items);
        Assert.Equal(data.Input.SourceSkuId, source.SkuId.Value); Assert.Equal("sealed", source.PackageKind); Assert.Equal(12, source.BaseUnitsPerPackage);
        Assert.Equal(data.Input.DestinationSkuId, destination.SkuId.Value); Assert.Equal("loose", destination.PackageKind); Assert.Equal(1, destination.BaseUnitsPerPackage);
        Assert.DoesNotContain(await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Maker.UserId)).ToListAsync(),
            row => row["permissions"].AsBsonArray.Contains(Permissions.StockCount) || row["permissions"].AsBsonArray.Contains(Permissions.CatalogManage));
        var preview = await data.Service.PreviewRuleAsync(data.Maker, data.Input); Assert.False(preview["physicalConversionAvailable"].AsBoolean);
        Assert.Equal(1L, preview["sourcePackages"].AsInt64); Assert.Equal(12L, preview["destinationPackages"].AsInt64); Assert.Equal(12L, preview["baseUnits"].AsInt64);
        var proposed = await data.Service.ProposeRuleAsync(data.Maker, data.Input, preview["binding"].AsString, Data.Meta("rule-propose-original"));
        var proposalId = proposed["proposalId"].AsInt64;
        Assert.False((await data.Service.ReadProposalAsync(data.Maker, proposalId))["canReview"].AsBoolean);
        var pending = await data.Service.ListProposalsAsync(data.Reviewer, Data.Location); var pendingItem = Assert.Single(pending["items"].AsBsonArray);
        Assert.True(pendingItem["canReview"].AsBoolean); Assert.True(pendingItem["decision"].IsBsonNull);
        Assert.True(pending["nextProposalId"].IsBsonNull); Assert.False(pending["physicalConversionAvailable"].AsBoolean);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.PreviewDecisionAsync(data.Maker, proposalId, PackConversionRuleDecision.Approve))).Code);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadProposalAsync(data.Foreign, proposalId))).Code);
        var effects = await data.EffectsAsync(); var restarted = data.NewService();
        Assert.Equal(proposed, await restarted.ProposeRuleAsync(data.Maker, data.Input, preview["binding"].AsString, Data.Meta("rule-propose-original")));
        Assert.Equal(proposed, await restarted.LookupAsync(data.Maker, B.Propose, "rule-propose-original")); Assert.Equal(effects, await data.EffectsAsync());
        Assert.Null(await restarted.LookupAsync(data.Foreign, B.Propose, "rule-propose-original"));
        var decisionPreview = await restarted.PreviewDecisionAsync(data.Reviewer, proposalId, PackConversionRuleDecision.Approve);
        Assert.True(decisionPreview["canReview"].AsBoolean);
        var approved = await restarted.DecideRuleAsync(data.Reviewer, proposalId, 1, PackConversionRuleDecision.Approve,
            decisionPreview["binding"].AsString, Data.Meta("rule-approve-original"));
        Assert.Equal(proposalId, approved["ruleId"].AsInt64); Assert.False(approved["physicalConversionAvailable"].AsBoolean);
        effects = await data.EffectsAsync(); restarted = data.NewService();
        Assert.Equal(approved, await restarted.DecideRuleAsync(data.Reviewer, proposalId, 1, PackConversionRuleDecision.Approve,
            decisionPreview["binding"].AsString, Data.Meta("rule-approve-original")));
        Assert.Equal(approved, await restarted.LookupAsync(data.Reviewer, B.Decide, "rule-approve-original")); Assert.Equal(effects, await data.EffectsAsync());
        var rule = Assert.Single((await restarted.ListRulesAsync(data.Maker, Data.Location))["items"].AsBsonArray);
        Assert.Equal(proposalId, rule["_id"].AsInt64); Assert.False(rule["physicalConversionAvailable"].AsBoolean);
        var history = await restarted.ReadProposalAsync(data.Maker, proposalId); Assert.False(history["canReview"].AsBoolean);
        Assert.Equal(approved["decisionId"], history["decision"]["_id"]); Assert.Equal(physical, await data.PhysicalAsync());
    }

    [Fact]
    public async Task Changed_catalog_cannot_be_approved_but_can_be_rejected_and_missing_exact_history_cannot_be_laundered()
    {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.Service.PreviewRuleAsync(data.Maker, data.Input);
        var proposed = await data.Service.ProposeRuleAsync(data.Maker, data.Input, preview["binding"].AsString, Data.Meta("rule-stale-catalog"));
        var id = proposed["proposalId"].AsInt64;
        // Genuine catalog revision, not a hand-edited positive rule/source.
        await data.Catalog.Service.ReviseDefinitionAsync(data.Catalog.Maker, new(new(data.Input.SourceSkuId), 1,
            CatalogAuthoringTestData.Definition(12, "Dus dengan label baru")), CatalogAuthoringTestData.Metadata("rule-source-revised"));
        Assert.Equal("pack_conversion_rule_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.PreviewDecisionAsync(data.Reviewer, id, PackConversionRuleDecision.Approve))).Code);
        var rejection = await data.Service.PreviewDecisionAsync(data.Reviewer, id, PackConversionRuleDecision.Reject);
        var rejected = await data.Service.DecideRuleAsync(data.Reviewer, id, 1, PackConversionRuleDecision.Reject,
            rejection["binding"].AsString, Data.Meta("rule-reject-stale")); Assert.True(rejected["ruleId"].IsBsonNull);
        Assert.Empty((await data.Service.ListRulesAsync(data.Maker, Data.Location))["items"].AsBsonArray);
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", rejected["decisionId"])).SingleAsync();
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", decision["auditId"])).SingleAsync();
        Assert.Equal(1L, (await data.Rows("auditEvents").DeleteOneAsync(audit)).DeletedCount);
        try {
            var broken = await data.EffectsAsync();
            Assert.Equal("pack_conversion_rule_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                data.Service.ReadProposalAsync(data.Reviewer, id))).Code);
            Assert.Equal("pack_conversion_rule_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                data.Service.LookupAsync(data.Reviewer, B.Decide, "rule-reject-stale"))).Code);
            Assert.Equal(broken, await data.EffectsAsync());
        } finally { await data.Rows("auditEvents").InsertOneAsync(audit); }
        Assert.Equal(rejected, await data.Service.LookupAsync(data.Reviewer, B.Decide, "rule-reject-stale"));
    }

    [Fact]
    public async Task Required_audit_and_final_actual_session_expiry_rollback_rule_history_receipt_and_catalog_fences()
    {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.Service.PreviewRuleAsync(data.Maker, data.Input);
        var original = await data.EffectsAsync(); var masters = await data.MastersAsync();
        var definition = await (await data.Catalog.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone();
        await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray {
            validator, new BsonDocument("kind", new BsonDocument("$ne", B.Propose)) }) } });
        try {
            await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.ProposeRuleAsync(data.Maker, data.Input, preview["binding"].AsString, Data.Meta("rule-audit-rollback")));
            Assert.Equal(original, await data.EffectsAsync()); Assert.Equal(masters, await data.MastersAsync());
        } finally { await data.Catalog.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", validator } }); }
        var now = data.Catalog.Clock.UtcNow; var deadline = now.AddSeconds(1);
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", deadline.UtcDateTime));
        var request = 0; var fired = 0; var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "PackRuleFinalSession-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (Volatile.Read(ref request) == 0 && value.DatabaseNamespace.DatabaseName == data.Catalog.Database.DatabaseNamespace.DatabaseName &&
                    value.CommandName == "update" && value.Command.GetValue("update", BsonNull.Value) == "skus")
                    foreach (var write in value.Command["updates"].AsBsonArray)
                        if (write["q"]["_id"] == data.Input.SourceSkuId && write["u"].AsBsonDocument.GetValue("$inc", new BsonDocument()).AsBsonDocument.GetValue("fence", 0L) == 1L)
                            Interlocked.CompareExchange(ref request, value.RequestId, 0);
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (request != 0 && value.RequestId == request && value.Reply.GetValue("nModified", 0).ToInt64() == 1 && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                    data.Catalog.Clock.UtcNow = deadline;
            });
        };
        using var client = new MongoClient(settings);
        try {
            var failure = await Record.ExceptionAsync(() => data.NewService(client).ProposeRuleAsync(data.Maker, data.Input, preview["binding"].AsString, Data.Meta("rule-final-session")));
            Assert.Equal(1, fired); Assert.Equal(deadline, data.Catalog.Clock.UtcNow);
            Assert.Equal("stale_or_inactive_session", Assert.IsType<AuthorizationDeniedException>(failure).Code);
            Assert.Equal(original, await data.EffectsAsync()); Assert.Equal(masters, await data.MastersAsync());
        } finally { data.Catalog.Clock.UtcNow = now; }
        Assert.Null(await data.Service.LookupAsync(data.Maker, B.Propose, "rule-final-session"));
    }

    internal sealed class Data(CatalogAuthoringTestData catalog) : IAsyncDisposable
    {
        internal static readonly GrantScope Location = CatalogAuthoringTestData.Warehouse;
        internal CatalogAuthoringTestData Catalog => catalog;
        internal ActorContext Maker = null!, Reviewer = null!, Foreign = null!;
        internal PackConversionRuleInput Input = null!;
        internal PackConversionRuleService Service => NewService();
        internal PackConversionRuleService NewService(MongoClient? client = null) {
            var selected = client ?? catalog.Client; var database = selected.GetDatabase(catalog.Database.DatabaseNamespace.DatabaseName);
            return new(database, new(selected), new(database), catalog.Clock);
        }
        internal IMongoCollection<BsonDocument> Rows(string name) => catalog.Rows(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic explicit rule preparation", "synthetic-pack-rule");
        internal static async Task<Data> CreateAsync(MongoFixture fixture) {
            var data = new Data(await CatalogAuthoringTestData.CreateAsync(fixture));
            try {
                Assert.True(CollectionMigrations.CurrentVersion >= 97);
                Assert.True(await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).IsCurrentAsync());
                await PackConversionRuleStorage.RequireAsync(data.Catalog.Database, default);
                var source = await data.Catalog.SkuAsync(12, "RULE-SEALED-12", "RULE");
                var family = await data.Catalog.RowAsync("productFamilies", source.FamilyId!.Value.Value);
                var destination = await data.Catalog.Service.CreateSkuAsync(data.Catalog.Maker, new(source.FamilyId.Value, family["revision"].AsInt64,
                    "RULE-LOOSE-1", "pencil-base", new("Satuan", "loose", "piece", 1, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1)),
                    CatalogAuthoringTestData.Metadata("rule-loose-created"));
                data.Input = new(Location, source.Id.Value, source.Version, source.CurrentDefinitionId!.Value.Value,
                    destination.Id.Value, destination.Version, destination.CurrentDefinitionId!.Value.Value, 1, 12);
                data.Maker = await data.Catalog.ActorAsync(970101, Location, Permissions.StockConvert);
                data.Reviewer = await data.Catalog.ActorAsync(970102, Location, Permissions.StockConvert);
                data.Foreign = await data.Catalog.ActorAsync(970103, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 201, WarehouseId: 202, ChannelId: 203), Permissions.StockConvert);
                await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, data.Maker.UserId, data.Reviewer.UserId, data.Foreign.UserId);
                return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal async Task<BsonDocument> EffectsAsync() {
            var state = new BsonDocument(); foreach (var table in new[] { B.Proposals, B.Decisions, B.Rules, "auditEvents", "idempotencyRecords", "outboxEvents" })
                state[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
        }
        internal async Task<BsonDocument> MastersAsync() {
            var state = new BsonDocument(); foreach (var table in new[] { "skus", "productFamilies", "catalogCategories", "variantDefinitions" })
                state[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
        }
        internal async Task<BsonDocument> PhysicalAsync() {
            var state = new BsonDocument(); foreach (var table in new[] { "inventoryPools", "stockBalances", "lots", "movements", "reservations", "journalEntries", "accountingPeriodHistory", "cashMovements",
                "procurementStockReceipts", "procurementStockValuationAllocations", "bankReceipts", "bankReceiptConfirmations", "paymentAllocations", "paymentReceiptBalances", "paymentInvoiceBalances" })
                state[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
        }
        public ValueTask DisposeAsync() => catalog.DisposeAsync();
    }
}

public sealed class PackConversionRulePolicyTests
{
    [Fact]
    public void Explicit_direction_location_distinct_identities_and_bounded_ratio_are_required() {
        var input = new PackConversionRuleInput(CatalogAuthoringTestData.Warehouse, 1, 1, 2, 3, 1, 4, 1, 12);
        Assert.Equal(input, PackConversionRulePolicy.Copy(input));
        foreach (var invalid in new[] { input with { SourcePackagesPerStep = 0 }, input with { DestinationPackagesPerStep = 1000001 },
            input with { DestinationSkuId = 1 }, input with { SourceDefinitionId = 4 }, input with { SourceSkuRevision = long.MaxValue },
            input with { Location = CatalogAuthoringTestData.Shop } }) Assert.Throws<DomainValidationException>(() => PackConversionRulePolicy.Copy(invalid));
    }
}

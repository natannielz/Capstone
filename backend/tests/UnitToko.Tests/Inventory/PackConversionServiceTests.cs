using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using Xunit.Abstractions;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionServiceTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Real_sealed_to_loose_command_conserves_physical_units_and_finite_value_and_replays_only_original_receipt() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await StockReleaseServiceTests.Data.CreateAsync(fixture, useSystemClock: true);
        var release = await data.ProposeAsync("conversion-source-release");
        await data.DecideAsync(release["proposalId"].AsInt64, "conversion-source-release-approved");
        var procurement = data.Valuation.Inspection.Receiving.Procurement;
        var sourceSku = await data.Rows("skus").Find(new BsonDocument("_id", procurement.Sku.Id.Value)).SingleAsync();
        var family = await data.Rows("productFamilies").Find(new BsonDocument("_id", sourceSku["familyId"])).SingleAsync();
        var loose = await procurement.Catalog.CreateSkuAsync(procurement.Author, new(new(family["_id"].AsInt64), family["revision"].AsInt64,
            "CONVERT-LOOSE-1", sourceSku["physicalItemKey"].AsString, CatalogAuthoringTestData.Definition(1, "Satuan lepas") with { PackageKind = "loose" }),
            StockReleaseServiceTests.Data.Meta("conversion-loose-catalog"));
        var maker = await data.Valuation.Base.AddActorAsync(104001, ProcurementRequestTestData.Scope, Permissions.StockConvert);
        var reviewer = await data.Valuation.Base.AddActorAsync(104002, ProcurementRequestTestData.Scope, Permissions.StockConvert);
        var foreign = await data.Valuation.Base.AddActorAsync(104003, new(GrantScopeKind.FulfillmentWarehouse, 100,
            ShopId: procurement.ForeignShopId, WarehouseId: procurement.ForeignWarehouseId, ChannelId: 103), Permissions.StockConvert);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId, foreign.UserId);
        var ruleService = new PackConversionRuleService(data.Database, data.Unit, new(data.Database), data.Clock);
        var ruleInput = new PackConversionRuleInput(ProcurementRequestTestData.Scope, procurement.Sku.Id.Value, sourceSku["revision"].AsInt64,
            sourceSku["currentDefinitionId"].AsInt64, loose.Id.Value, loose.Version, loose.CurrentDefinitionId!.Value.Value, 1, 12);
        var rulePreview = await ruleService.PreviewRuleAsync(maker, ruleInput);
        var ruleProposal = await ruleService.ProposeRuleAsync(maker, ruleInput, rulePreview["binding"].AsString, StockReleaseServiceTests.Data.Meta("conversion-rule-proposed"));
        var decision = await ruleService.PreviewDecisionAsync(reviewer, ruleProposal["proposalId"].AsInt64, PackConversionRuleDecision.Approve);
        var approved = await ruleService.DecideRuleAsync(reviewer, ruleProposal["proposalId"].AsInt64, 1, PackConversionRuleDecision.Approve,
            decision["binding"].AsString, StockReleaseServiceTests.Data.Meta("conversion-rule-approved"));
        PackConversionService Service() => new(data.Database, data.Unit, new(data.Database), data.Clock, procurement.Catalog,
            data.Valuation.Inspection.Receiving.Orders.Service, data.Valuation.Sources, data.Valuation.Service, data.NewService());
        var service = Service(); var sourceBefore = await data.BalanceAsync();
        var input = new PackConversionInput(ProcurementRequestTestData.Scope, approved["ruleId"].AsInt64, 1, sourceBefore["_id"].AsInt64, 1);
        var preview = await service.PreviewAsync(maker, input);
        Assert.Equal(1L, preview["quantities"]["sourcePackages"].AsInt64); Assert.Equal(12L, preview["quantities"]["destinationPackages"].AsInt64);
        var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewAsync(foreign, input));
        var expected = B.Expected(preview["expected"]); var metadata = StockReleaseServiceTests.Data.Meta("conversion-original-key");
        var result = await service.ExecuteAsync(maker, input, expected, preview["binding"].AsString, metadata);
        Assert.Equal("converted", result["status"].AsString); Assert.Equal("11250.00", result["transferredValue"].AsString);
        var after = await data.BalanceAsync(); Assert.Equal(2L, after["onHand"].AsInt64); Assert.Equal(2L, after["held"].AsInt64);
        Assert.Equal(0L, after["reserved"].AsInt64); Assert.Equal(0L, after["expired"].AsInt64);
        var destination = await data.Rows("stockBalances").Find(new BsonDocument("_id", result["destinationBalanceId"])).SingleAsync();
        Assert.Equal(12L, destination["onHand"].AsInt64); Assert.Equal(0L, destination["held"].AsInt64); Assert.Equal(0L, destination["reserved"].AsInt64);
        var sourceValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", after["_id"])).SingleAsync();
        var destinationValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", destination["_id"])).SingleAsync();
        Assert.Equal(0L, sourceValue["remainingPackages"].AsInt64); Assert.Equal("0.00", sourceValue["remainingValue"].AsString);
        Assert.Equal(12L, destinationValue["remainingPackages"].AsInt64); Assert.Equal("11250.00", destinationValue["remainingValue"].AsString);
        var committed = Assert.Single(await data.Rows(B.Conversions).Find(new BsonDocument()).ToListAsync()); Assert.Equal("committed", committed["phase"].AsString);
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument("operation", "convert")));
        var preserved = await data.BusinessEffectsAsync(); var restarted = Service();
        Assert.Equal(result, await restarted.ExecuteAsync(maker, input, expected, preview["binding"].AsString, metadata));
        Assert.Equal(result, await restarted.RecoverAsync(maker, metadata.IdempotencyKey));
        Assert.Equal(preserved, await data.BusinessEffectsAsync());
        Assert.Equal("insufficient_available_stock", (await Assert.ThrowsAsync<DomainValidationException>(() => restarted.PreviewAsync(maker, input))).Code);
        Assert.Equal(preserved, await data.BusinessEffectsAsync());
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Duplicate_and_orphan_original_effect_links_refuse_recovery_without_changing_stock_value_or_documents() {
        await using var prepared = await PreparedConversion.CreateAsync(fixture);
        var data = prepared.Data; var row = await data.Rows(B.Conversions).Find(new BsonDocument()).SingleAsync();
        var result = prepared.Result!; var id = result["conversionId"].AsInt64;
        Assert.NotNull(await prepared.Service().ReadAsync(prepared.Maker, id));
        Assert.Equal(result, await prepared.Service().RecoverAsync(prepared.Maker, prepared.Metadata.IdempotencyKey));
        var business = await prepared.StateAsync(); var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        foreach (var table in new[] { "auditEvents", "outboxEvents", "idempotencyRecords" }) {
            var filter = table == "auditEvents" ? new BsonDocument("_id", row["auditId"]) : table == "outboxEvents"
                ? new BsonDocument("sourceKey", B.Operation + ":" + id)
                : new BsonDocument { { "actorId", prepared.Maker.UserId }, { "operation", B.Operation }, { "key", prepared.Metadata.IdempotencyKey } };
            var duplicate = (await data.Rows(table).Find(filter).SingleAsync()).DeepClone().AsBsonDocument;
            duplicate["_id"] = table == "auditEvents" ? new BsonInt64((await new IdAllocator(data.Database).NextAsync("auditEvents")).Value) : new BsonObjectId(ObjectId.GenerateNewId());
            if (table == "outboxEvents") duplicate["sourceKey"] = B.Operation + ":duplicate-" + id;
            if (table == "idempotencyRecords") duplicate["key"] = prepared.Metadata.IdempotencyKey + "-duplicate";
            await data.Rows(table).InsertOneAsync(duplicate);
            var faulty = await prepared.StateAsync();
            Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                prepared.Service().RecoverAsync(prepared.Maker, prepared.Metadata.IdempotencyKey))).Code);
            Assert.Equal(faulty, await prepared.StateAsync());
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", duplicate["_id"]))).DeletedCount);
        }
        Assert.Equal(business, await prepared.StateAsync());
        // Deliberately orphan the genuine command's effects. Production recovery
        // must refuse; the fixture is disposed with this fault still present.
        Assert.Equal(1L, (await data.Rows(B.Conversions).DeleteOneAsync(new BsonDocument("_id", id))).DeletedCount);
        // Remove the first refusal signal after each check: original receipt,
        // then outbox, leaving the actual audit as the final independent orphan.
        foreach (var table in new[] { "idempotencyRecords", "outboxEvents", "auditEvents" }) {
            var filter = table == "auditEvents" ? new BsonDocument("_id", row["auditId"]) : table == "outboxEvents"
                ? new BsonDocument("sourceKey", B.Operation + ":" + id)
                : new BsonDocument { { "actorId", prepared.Maker.UserId }, { "operation", B.Operation }, { "key", prepared.Metadata.IdempotencyKey } };
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(filter));
            var orphaned = await prepared.StateAsync();
            Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                prepared.Service().ReadAsync(prepared.Maker, id))).Code);
            Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                prepared.Service().RecoverAsync(prepared.Maker, prepared.Metadata.IdempotencyKey))).Code);
            Assert.Equal(orphaned, await prepared.StateAsync());
            if (table != "auditEvents") Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
        }
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Warm_owner_history_refuses_source_lot_and_private_pin_deletion_in_the_same_actual_transaction() {
        await using var prepared = await PreparedConversion.CreateAsync(fixture);
        var data = prepared.Data; var row = await data.Rows(B.Conversions).Find(new BsonDocument()).SingleAsync();
        var inspection = await data.Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", data.Valuation.Documents.InspectionId)).SingleAsync();
        var state = await prepared.StateAsync(); var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        foreach (var table in new[] { "lots", "evidencePins" }) {
            var id = table == "lots" ? row["sourceLot"]["_id"] : inspection["pinId"];
            var original = await data.Rows(table).Find(new BsonDocument("_id", id)).SingleAsync();
            var warmed = 0; var deleted = 0;
            async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
                var service = prepared.Service();
                var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
                await service.AuthenticateSourceAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
                warmed++;
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, new BsonDocument("_id", id), cancellationToken: ct)).DeletedCount); deleted++;
                await service.AuthenticateSourceAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
                return true;
            }, default);
            if (table == "lots")
                Assert.Equal("held_receipt_movement_history_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(Attempt)).Code);
            else
                Assert.Equal("evidence_validation_integrity", (await Assert.ThrowsAsync<PersistenceConflictException>(Attempt)).Code);
            Assert.Equal(1, warmed); Assert.Equal(1, deleted);
            Assert.Equal(original, await data.Rows(table).Find(new BsonDocument("_id", id)).SingleAsync());
            Assert.Equal(state, await prepared.StateAsync());
        }
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Source_expiry_after_real_result_receipt_update_rolls_back_both_physical_legs_value_and_effects() {
        var inspectedExpiry = AccountingCalendar.BusinessDate(DateTimeOffset.UtcNow).AddDays(1);
        await using var prepared = await PreparedConversion.CreateAsync(fixture, execute: false, inspectedExpiry: inspectedExpiry);
        var data = prepared.Data;
        var lot = await data.Rows("lots").Find(new BsonDocument("_id", data.Valuation.Receipt["lotId"])).SingleAsync();
        Assert.Equal(inspectedExpiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), lot["expiresOn"].AsString);
        var expiresAt = new DateTimeOffset(inspectedExpiry.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var people = await data.Unit.ExecuteAsync((tx, ct) => new UnitToko.Platform.Identity.PersonRegistryService(data.Database, data.Clock)
            .ResolveProofsAsync(tx, [prepared.Maker.UserId], ct), default);
        Assert.True(people.ValidUntil is null || expiresAt < people.ValidUntil.Value);
        var state = await prepared.StateAsync(); var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        var authority = new BsonDocument();
        foreach (var table in new[] { "authorizationScopes", "authorizationGrants", "memberships", "identities", "inventoryPools", "stockSourceTopologyFences" })
            authority[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var request = 0; var observed = 0; var beforeClock = data.Clock.UtcNow;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "pack-conversion-late-expiry-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName == "update" &&
                    command.Command.GetValue("update", BsonNull.Value) == "idempotencyRecords" && command.Command["updates"].AsBsonArray.Any(update =>
                        update["q"].AsBsonDocument.GetValue("operation", BsonNull.Value) == B.Operation &&
                        update["q"].AsBsonDocument.GetValue("key", BsonNull.Value) == prepared.Metadata.IdempotencyKey &&
                        update["u"].AsBsonDocument.GetValue("$set", new BsonDocument()).AsBsonDocument.GetValue("result", new BsonDocument()).AsBsonDocument.Contains("conversionId")))
                    request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (command.CommandName == "update" && command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0)
                    data.Clock.UtcNow = expiresAt;
            });
        };
        using var client = new MongoClient(settings);
        try {
            Assert.Equal(B.Changed().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(() => prepared.Service(client).ExecuteAsync(prepared.Maker,
                prepared.Input, B.Expected(prepared.Preview["expected"]), prepared.Preview["binding"].AsString, prepared.Metadata))).Code);
        } finally { data.Clock.UtcNow = beforeClock; }
        Assert.Equal(1, observed); Assert.Equal(state, await prepared.StateAsync());
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
        foreach (var table in authority.Names)
            Assert.Equal(authority[table], new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Null(await prepared.Service().RecoverAsync(prepared.Maker, prepared.Metadata.IdempotencyKey));
    }

    [WindowsStorageFact]
    public async Task Two_distinct_commands_for_one_available_package_have_one_finite_winner_and_no_losing_physical_or_value_effects() {
        await using var prepared = await PreparedConversion.CreateAsync(fixture, execute: false);
        var data = prepared.Data;
        var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        async Task<(BsonDocument? Result, Exception? Failure)> Attempt(string key) {
            try { return (await prepared.Service().ExecuteAsync(prepared.Maker, prepared.Input, B.Expected(prepared.Preview["expected"]),
                prepared.Preview["binding"].AsString, StockReleaseServiceTests.Data.Meta(key)), null); }
            catch (Exception failure) { return (null, failure); }
        }
        var attempts = await Task.WhenAll(Attempt("conversion-race-first"), Attempt("conversion-race-second"));
        var winner = Assert.Single(attempts, attempt => attempt.Result is not null).Result!;
        var loser = Assert.Single(attempts, attempt => attempt.Failure is not null).Failure!;
        // The shared transaction owner may exhaust its bounded retry before
        // another attempt can reach the stock-specific current-state refusal.
        var loserCode = loser switch { PersistenceConflictException conflict => conflict.Code,
            DomainValidationException validation => validation.Code, _ => loser.GetType().Name };
        output.WriteLine("FINITE_CONVERSION_RACE_LOSER_CODE " + loserCode);
        Assert.True(loser is PersistenceConflictException { Code: "pack_conversion_changed" or "version_conflict" or "stock_revision_conflict" } or
            DomainValidationException { Code: "insufficient_available_stock" }, loserCode);
        Assert.Equal("11250.00", winner["transferredValue"].AsString);
        Assert.Equal(1L, await data.Rows(B.Conversions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument("operation", "convert")));
        Assert.Equal(1L, await data.Rows("lots").CountDocumentsAsync(new BsonDocument("origin", "packageConversion")));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, B.Operation)));
        var source = await data.BalanceAsync();
        Assert.Equal(2L, source["onHand"].AsInt64); Assert.Equal(2L, source["held"].AsInt64);
        Assert.Equal(0L, source["reserved"].AsInt64); Assert.Equal(0L, source["expired"].AsInt64);
        var destination = await data.Rows("stockBalances").Find(new BsonDocument("_id", winner["destinationBalanceId"])).SingleAsync();
        Assert.Equal(12L, destination["onHand"].AsInt64);
        var sourceValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", source["_id"])).SingleAsync();
        var destinationValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", destination["_id"])).SingleAsync();
        Assert.Equal(0L, sourceValue["remainingPackages"].AsInt64); Assert.Equal("0.00", sourceValue["remainingValue"].AsString);
        Assert.Equal(12L, destinationValue["remainingPackages"].AsInt64); Assert.Equal("11250.00", destinationValue["remainingValue"].AsString);
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    internal sealed class PreparedConversion(StockReleaseServiceTests.Data data, ActorContext maker, PackConversionInput input,
        BsonDocument preview, AuthorizationCommandMetadata metadata) : IAsyncDisposable {
        internal StockReleaseServiceTests.Data Data => data;
        internal ActorContext Maker { get; } = maker;
        internal PackConversionInput Input { get; } = input;
        internal BsonDocument Preview { get; } = preview;
        internal AuthorizationCommandMetadata Metadata { get; } = metadata;
        internal BsonDocument? Result;
        internal PackConversionService Service(MongoClient? client = null) {
            var procurement = data.Valuation.Inspection.Receiving.Procurement;
            if (client is null) return new(data.Database, data.Unit, new(data.Database), data.Clock, procurement.Catalog,
                data.Valuation.Inspection.Receiving.Orders.Service, data.Valuation.Sources, data.Valuation.Service, data.NewService());
            var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
            var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, data.Clock);
            var requests = new ProcurementRequestService(database, unit, ids, data.Clock, catalog);
            var orders = new PurchaseOrderService(database, unit, ids, data.Clock, catalog, requests, new(database, unit, ids, data.Clock, catalog));
            var sources = new StockSourceSetupService(database, unit, ids, data.Clock);
            var receiving = new ProcurementReceivingService(database, unit, ids, data.Clock, catalog, orders, requests);
            var valuation = new ProcurementStockValuationService(database, unit, ids, data.Clock, catalog, orders, receiving, sources);
            var releases = new StockReleaseService(database, unit, ids, data.Clock, catalog, orders, sources, valuation);
            return new(database, unit, ids, data.Clock, catalog, orders, sources, valuation, releases);
        }
        internal async Task<BsonDocument> StateAsync() {
            var state = await data.BusinessEffectsAsync();
            foreach (var table in new[] { B.Conversions, "lots" })
                state[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                state["conversion-" + table] = new BsonArray(await data.Rows(table).Find(new BsonDocument(field, B.Operation)).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        internal static async Task<PreparedConversion> CreateAsync(MongoFixture fixture, bool execute = true, DateOnly? inspectedExpiry = null,
            bool includeCogsMapping = false, long reviewedSafetyBufferBaseUnits = 12, bool useSystemClock = false, bool includeCustomerMappings = false) {
            var data = await StockReleaseServiceTests.Data.CreateAsync(fixture, useSystemClock: true, inspectedExpiry: inspectedExpiry,
                includeCogsMapping: includeCogsMapping, reviewedSafetyBufferBaseUnits: reviewedSafetyBufferBaseUnits, includeCustomerMappings: includeCustomerMappings);
            try {
                if (!useSystemClock) { var now = data.Clock.UtcNow; data.Clock.UseSystemClock = false; data.Clock.UtcNow = now; }
                var release = await data.ProposeAsync("conversion-guard-source-release");
                await data.DecideAsync(release["proposalId"].AsInt64, "conversion-guard-source-approved");
                var procurement = data.Valuation.Inspection.Receiving.Procurement;
                var sku = await data.Rows("skus").Find(new BsonDocument("_id", procurement.Sku.Id.Value)).SingleAsync();
                var family = await data.Rows("productFamilies").Find(new BsonDocument("_id", sku["familyId"])).SingleAsync();
                var loose = await procurement.Catalog.CreateSkuAsync(procurement.Author, new(new(family["_id"].AsInt64), family["revision"].AsInt64,
                    "CONVERSION-GUARD-LOOSE", sku["physicalItemKey"].AsString, CatalogAuthoringTestData.Definition(1, "Satuan lepas") with { PackageKind = "loose" }),
                    StockReleaseServiceTests.Data.Meta("conversion-guard-loose"));
                var maker = await data.Valuation.Base.AddActorAsync(104101, ProcurementRequestTestData.Scope, Permissions.StockConvert);
                var reviewer = await data.Valuation.Base.AddActorAsync(104102, ProcurementRequestTestData.Scope, Permissions.StockConvert);
                await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId);
                var rules = new PackConversionRuleService(data.Database, data.Unit, new(data.Database), data.Clock);
                var ruleInput = new PackConversionRuleInput(ProcurementRequestTestData.Scope, procurement.Sku.Id.Value, sku["revision"].AsInt64,
                    sku["currentDefinitionId"].AsInt64, loose.Id.Value, loose.Version, loose.CurrentDefinitionId!.Value.Value, 1, 12);
                var rulePreview = await rules.PreviewRuleAsync(maker, ruleInput);
                var proposed = await rules.ProposeRuleAsync(maker, ruleInput, rulePreview["binding"].AsString, StockReleaseServiceTests.Data.Meta("conversion-guard-rule-proposed"));
                var review = await rules.PreviewDecisionAsync(reviewer, proposed["proposalId"].AsInt64, PackConversionRuleDecision.Approve);
                var approved = await rules.DecideRuleAsync(reviewer, proposed["proposalId"].AsInt64, 1, PackConversionRuleDecision.Approve,
                    review["binding"].AsString, StockReleaseServiceTests.Data.Meta("conversion-guard-rule-approved"));
                var balance = await data.BalanceAsync(); var input = new PackConversionInput(ProcurementRequestTestData.Scope, approved["ruleId"].AsInt64, 1, balance["_id"].AsInt64, 1);
                var service = new PackConversionService(data.Database, data.Unit, new(data.Database), data.Clock, procurement.Catalog,
                    data.Valuation.Inspection.Receiving.Orders.Service, data.Valuation.Sources, data.Valuation.Service, data.NewService());
                var preview = await service.PreviewAsync(maker, input);
                var prepared = new PreparedConversion(data, maker, input, preview, StockReleaseServiceTests.Data.Meta("conversion-guard-original-key"));
                if (execute) prepared.Result = await service.ExecuteAsync(maker, input, B.Expected(preview["expected"]), preview["binding"].AsString, prepared.Metadata);
                return prepared;
            } catch { await data.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => data.DisposeAsync();
    }
}

using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using D = UnitToko.Tests.Accounting.AccountingBookSetupTestData;

namespace UnitToko.Tests.Fulfillment;

[Collection("Mongo")]
public sealed class ShipmentDispatchAccountingTests(MongoFixture fixture)
{
    private static AccountingBookSetupInput Input(D data, string inventoryCode = "ASTOCK", string cogsCode = "ECOGS") => data.Input() with {
        Mappings = [new("inventory", inventoryCode), new("cogs", cogsCode), new("payable", "LAP"), new("supplier_debt", "LAP")]
    };
    private static DateOnly Date(D data) => DateOnly.ParseExact(data.Input().InitialMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static async Task<BsonDocument> ApproveAsync(D data, AccountingBookSetupInput? input = null) =>
        await data.DecideAsync(await data.ProposeAsync("dispatch-book", input ?? Input(data)), "dispatch-book-approved");

    [Fact]
    public async Task Genuine_independent_book_approval_supplies_exact_accounts_with_defensive_binding_and_no_financial_effect()
    {
        await using var data = await D.CreateAsync(fixture); var approved = await ApproveAsync(data);
        var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        var originalChart = await data.Rows(D.Charts).Find(new BsonDocument()).SingleAsync();
        var originalBook = await data.Rows(D.Books).Find(new BsonDocument()).SingleAsync();
        var snapshot = await data.Unit.ExecuteAsync(async (tx, ct) => {
            var binding = await helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct);
            var value = binding.Snapshot;
            Assert.Equal(approved["proposalId"], value["setupProposalId"]); Assert.Equal(approved["decisionId"], value["setupDecisionId"]);
            Assert.Equal(approved["bookId"], value["book"]["_id"]); Assert.Equal(approved["chartId"], value["chart"]["_id"]);
            Assert.Equal("inventory", value["inventory"]["mappingKey"]); Assert.Equal("ASTOCK", value["inventory"]["accountCode"]);
            Assert.Equal("cogs", value["cogs"]["mappingKey"]); Assert.Equal("ECOGS", value["cogs"]["accountCode"]);
            Assert.NotEqual(value["inventory"]["accountId"], value["cogs"]["accountId"]);
            value["inventory"]["accountCode"] = "MUTATED-RETURNED-COPY";
            Assert.Equal("ASTOCK", binding.Snapshot["inventory"]["accountCode"]);
            var expectedInventory = binding.Snapshot["inventory"].AsBsonDocument;
            var lineageBound = await helper.ReadAndFenceAsync(tx, D.Key, Date(data), expectedInventory, ct);
            expectedInventory["accountId"] = long.MaxValue;
            await helper.RequireCurrentAsync(tx, binding, ct); await helper.RequireCurrentAsync(tx, lineageBound, ct);
            return binding.Snapshot;
        });
        Assert.Equal(originalChart, await data.Rows(D.Charts).Find(new BsonDocument()).SingleAsync());
        Assert.True((await data.Rows(D.Books).Find(new BsonDocument()).SingleAsync())["fence"].AsInt64 > originalBook["fence"].AsInt64);
        Assert.Equal(Date(data).ToString("yyyy-MM", CultureInfo.InvariantCulture), snapshot["periodMonth"]);
        await data.NoFinancialEffectsAsync();
    }

    [Theory]
    [InlineData("inventory")] [InlineData("cogs")]
    public async Task Genuine_approved_setup_without_a_required_dispatch_mapping_is_refused(string absent)
    {
        await using var data = await D.CreateAsync(fixture); var input = Input(data);
        await ApproveAsync(data, input with { Mappings = [.. input.Mappings.Where(mapping => mapping.MappingKey != absent)] });
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync((tx, ct) => helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct)));
        Assert.Equal("shipment_dispatch_accounting_changed", error.Code); Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Explicitly_approved_shared_inventory_and_cogs_account_is_refused_without_reinterpreting_its_code()
    {
        await using var data = await D.CreateAsync(fixture); await ApproveAsync(data, Input(data, cogsCode: "ASTOCK"));
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync((tx, ct) => helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct)));
        Assert.Equal("shipment_dispatch_accounting_changed", error.Code); Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Exact_operational_scope_and_existing_inventory_lineage_are_required()
    {
        await using var data = await D.CreateAsync(fixture); await ApproveAsync(data);
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync((tx, ct) =>
            helper.ReadAndFenceAsync(tx, D.Key with { ShopId = data.ForeignShopId }, Date(data), ct: ct)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var binding = await helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct);
            var wrongInventory = binding.Snapshot["inventory"].DeepClone().AsBsonDocument;
            wrongInventory["accountId"] = binding.Snapshot["cogs"]["accountId"];
            return await helper.ReadAndFenceAsync(tx, D.Key, Date(data), wrongInventory, ct);
        }));
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Theory]
    [InlineData("approval")] [InlineData("posting-date")]
    public async Task Future_approval_or_future_Jakarta_posting_date_is_refused(string future)
    {
        await using var data = await D.CreateAsync(fixture); await ApproveAsync(data);
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        if (future == "approval") data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(-1);
        var date = future == "posting-date" ? AccountingCalendar.BusinessDate(data.Clock.UtcNow).AddDays(1) : Date(data);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync((tx, ct) => helper.ReadAndFenceAsync(tx, D.Key, date, ct: ct)));
        Assert.Equal("shipment_dispatch_accounting_changed", error.Code);
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Theory]
    [InlineData("inactive-book")] [InlineData("inactive-chart")] [InlineData("replaced-chart")] [InlineData("closed-period")]
    public async Task Untrusted_changes_to_genuine_approved_core_are_refused_and_rolled_back(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var approved = await ApproveAsync(data);
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        var originalPeriod = await data.Rows(D.Periods).Find(new BsonDocument("_id", approved["periodId"])).SingleAsync();
        var helperReads = 0;
        var error = await Record.ExceptionAsync(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            if (fault == "inactive-book") await data.Rows(D.Books).UpdateOneAsync(tx, new BsonDocument("_id", approved["bookId"]), Builders<BsonDocument>.Update.Set("active", false), cancellationToken: ct);
            else if (fault == "inactive-chart") await data.Rows(D.Charts).UpdateOneAsync(tx, new BsonDocument("_id", approved["chartId"]), Builders<BsonDocument>.Update.Set("active", false), cancellationToken: ct);
            else if (fault == "replaced-chart") await data.Rows(D.Books).UpdateOneAsync(tx, new BsonDocument("_id", approved["bookId"]), Builders<BsonDocument>.Update.Set("chartVersionId", long.MaxValue), cancellationToken: ct);
            // Deliberately untrusted negative corruption only: an owning close also
            // requires approval/history fields, which this counterfeit state omits.
            else await data.Rows(D.Periods).UpdateOneAsync(tx, new BsonDocument("_id", approved["periodId"]), Builders<BsonDocument>.Update.Set("state", "closed"),
                new UpdateOptions { BypassDocumentValidation = true }, ct);
            helperReads++;
            return await helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct);
        }));
        Assert.Equal(1, helperReads);
        Assert.True(error is PersistenceConflictException or DomainValidationException, error?.ToString());
        Assert.Equal(originalPeriod, await data.Rows(D.Periods).Find(new BsonDocument("_id", approved["periodId"])).SingleAsync());
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Current_completion_rechecks_clock_and_approved_account_rows_after_await_without_rebasing()
    {
        await using var data = await D.CreateAsync(fixture); var approved = await ApproveAsync(data);
        var before = await data.StateAsync(); var helper = new ShipmentDispatchAccounting(data.Database, data.Clock);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var binding = await helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct); await Task.Yield();
            await data.Rows(D.Charts).UpdateOneAsync(tx, new BsonDocument("_id", approved["chartId"]),
                Builders<BsonDocument>.Update.Set("mappings.0.accountCode", "REPLACED"), cancellationToken: ct);
            await helper.RequireCurrentAsync(tx, binding, ct); return true;
        }));
        Assert.Equal(before, await data.StateAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var binding = await helper.ReadAndFenceAsync(tx, D.Key, Date(data), ct: ct); await Task.Yield();
            data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(-1); await helper.RequireCurrentAsync(tx, binding, ct); return true;
        }));
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }
}

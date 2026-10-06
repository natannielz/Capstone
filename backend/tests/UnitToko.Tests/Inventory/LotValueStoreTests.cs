using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;
[Collection("Mongo")]
public sealed class LotValueStoreTests(MongoFixture fixture)
{
    [Fact]
    public async Task An_existing_unvalued_physical_lot_cannot_infer_available_value_or_create_a_value_owner()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var physical = InventoryGraphTests.Balance();
        var observed = 0; var unit = new MongoUnitOfWork(fixture.Client); var store = new LotValueStore(data.Database, new(data.Database), data.Clock);
        Task Authenticate(IMongoDatabase database, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) {
            observed++; throw new InvalidOperationException("Missing value history must never invent an original source");
        }
        Assert.Equal("lot_value_provenance_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync(
            (tx, ct) => store.ReadAvailableAsync(tx, physical, Authenticate, ct)))).Code);
        Assert.Equal(0, observed);
        foreach (var name in LotValueStore.Collections) Assert.False(await Rows(data, name).Find(new BsonDocument()).AnyAsync());
        Assert.Null(await unit.ExecuteAsync((tx, ct) => store.ReadHeldAsync(tx, 991L, Authenticate, ct)));
    }
    [Fact]
    public async Task A_schema_valid_descriptive_value_head_without_its_owning_history_is_refused_without_repair()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var physical = InventoryGraphTests.Balance();
        // Explicit negative corruption: these descriptive bytes are never a positive authority.
        var counterfeit = Head(physical); await Rows(data, LotValueStore.AvailableHeads).InsertOneAsync(counterfeit);
        var unit = new MongoUnitOfWork(fixture.Client); var store = new LotValueStore(data.Database, new(data.Database), data.Clock);
        Task Authenticate(IMongoDatabase database, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
            throw new InvalidOperationException("No authentic source or transition was produced");
        Assert.Equal("lot_value_provenance_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync(
            (tx, ct) => store.ReadAvailableAsync(tx, physical, Authenticate, ct)))).Code);
        Assert.Equal(counterfeit, await Rows(data, LotValueStore.AvailableHeads).Find(new BsonDocument()).SingleAsync());
        Assert.False(await Rows(data, LotValueStore.Transitions).Find(new BsonDocument()).AnyAsync());
        Assert.False(await Rows(data, LotValueStore.HeldHeads).Find(new BsonDocument()).AnyAsync());
    }
    [Theory]
    [InlineData("floating-quantity")]
    [InlineData("numeric-money")]
    [InlineData("extra-field")]
    [InlineData("noncanonical-money")]
    [InlineData("overflow-money")]
    public async Task Strict_value_storage_rejects_ambiguous_or_noncanonical_quantities_and_money(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var row = Head(InventoryGraphTests.Balance());
        switch (fault) {
            case "floating-quantity": row["remainingPackages"] = 10d; break;
            case "numeric-money": row["remainingValue"] = new BsonDecimal128(100m); break;
            case "extra-field": row["mintApproved"] = true; break;
            case "noncanonical-money": row["remainingValue"] = "0100.0"; break;
            case "overflow-money": row["remainingValue"] = "1000000000000000.00"; break;
        }
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(data, LotValueStore.AvailableHeads).InsertOneAsync(row))).WriteError.Code);
        Assert.False(await Rows(data, LotValueStore.AvailableHeads).Find(new BsonDocument()).AnyAsync());
    }
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static BsonDocument Head(InventoryBalanceDocument physical) => new() {
        { "_id", physical.Id }, { "schemaVersion", 1 }, { "kind", "inventory.lotvalue.available" }, { "poolId", physical.PhysicalPoolId },
        { "balanceId", physical.Id }, { "key", LotValueStore.Key(physical.Balance.Key) }, { "remainingPackages", 10L }, { "remainingValue", "100.00" },
        { "revision", 1L }, { "fence", 1L }, { "lastTransitionId", 999L }, { "lastHash", new string('a', 64) },
        { "updatedAt", new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc) } };
}

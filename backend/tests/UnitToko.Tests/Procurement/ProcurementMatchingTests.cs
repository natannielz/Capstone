using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using D = UnitToko.Tests.Procurement.ProcurementRequestTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementMatchingTests(MongoFixture fixture)
{
    [Fact]
    public async Task Both_scoped_pickers_explain_ambiguous_names_without_changing_selected_package_or_creating_mapping()
    {
        await using var supplier = await SupplierTestData.CreateAsync(fixture); var data = supplier.Procurement;
        var before = await data.StateAsync(); var supplierBefore = await supplier.StateAsync();
        var exact = await data.Service.PackagesAsync(data.Maker, D.Location, 0, 25, "PROC-PENCIL-12");
        Assert.Equal(data.Sku.Id.Value, exact["items"][0]["skuId"].AsInt64);
        Assert.Equal("exact_code", exact["matching"]["items"][0]["kind"].AsString);
        var byName = await data.Service.PackagesAsync(data.Maker, D.Location, 0, 25, "Pensil pengadaan");
        Assert.Equal(2, byName["items"].AsBsonArray.Count);
        Assert.All(byName["matching"]["items"].AsBsonArray, hint => Assert.Equal("exact_name", hint["kind"].AsString));
        var typo = await data.Service.PackagesAsync(data.Maker, D.Location, 0, 25, "Penzil pengadaan");
        Assert.Equal(ProductMatchRule.Version, typo["matching"]["ruleVersion"].AsString); Assert.False(typo["matching"]["limited"].AsBoolean);
        Assert.Equal(new[] { 1, 12 }, typo["items"].AsBsonArray.Select(item => item["baseUnitsPerPackage"].AsInt32).Order());
        Assert.All(typo["matching"]["items"].AsBsonArray, hint => { Assert.Equal("similar_name", hint["kind"].AsString); Assert.Equal(1, hint["editDistance"].AsInt32); });
        Assert.Equal(typo, await supplier.Service.PackagesAsync(supplier.Maker, SupplierAccess.Propose, D.Location, 0, 25, "Penzil pengadaan"));
        foreach (var query in new[] { "FOREIGN-PENCIL-12", "zzzzzzzzzzzz", "12 pieces guessed" })
            Assert.Empty((await data.Service.PackagesAsync(data.Maker, D.Location, 0, 25, query))["items"].AsBsonArray);
        var page = await data.Service.PackagesAsync(data.Maker, D.Location, 1, 1, "Penzil pengadaan");
        Assert.Single(page["items"].AsBsonArray); Assert.False(page["hasMore"].AsBoolean);
        Assert.Equal(page["items"][0]["skuId"], page["matching"]["items"][0]["skuId"]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PackagesAsync(data.Outsider, D.Location, 0, 25, "Penzil"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => supplier.Service.PackagesAsync(supplier.Outsider, SupplierAccess.Propose, D.Location, 0, 25, "Penzil"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => supplier.Service.PackagesAsync(data.Maker, SupplierAccess.Propose, D.Location, 0, 25, "Penzil"));
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(supplierBefore, await supplier.StateAsync());
        var choice = typo["items"].AsBsonArray.Single(item => item["skuId"].AsInt64 == data.Sku.Id.Value);
        var input = data.Input(ProcurementRoute.Ddo, ProcurementRequestKind.PurchaseRequest) with { Lines = [new(choice["skuId"].AsInt64,
            choice["skuRevision"].AsInt64, choice["definitionId"].AsInt64, choice["definitionVersion"].AsInt64, 2, null)] };
        var submitted = await data.SubmitAsync("matching-explicit-choice", input);
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Maker, D.Prefix + "submit", "matching-explicit-choice"));
        var refreshed = await data.Service.ReadAsync(data.Reader, submitted["requestId"].AsInt64);
        Assert.Equal(12, refreshed["lines"][0]["baseUnitsPerPackage"].AsInt32); Assert.Equal(2, refreshed["lines"][0]["packages"].AsInt32);
        var stored = await data.Rows(D.Requests).Find(new BsonDocument("_id", submitted["requestId"])).SingleAsync();
        Assert.False(stored.Contains("matching")); Assert.Equal(supplierBefore, await supplier.StateAsync()); await data.NoBusinessEffectsAsync();
    }
}

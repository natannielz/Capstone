using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementApprovedValuationAllocationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Exact_approved_allocation_requires_original_private_sources_and_posted_journal_without_releasing_stock()
    {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("selected-acquisition-value");
        var approved = await data.DecideAsync(proposed["proposalId"].AsInt64, "selected-acquisition-value-approved");
        var id = approved["allocationId"].AsInt64;
        var before = await data.StateAsync();
        var exact = await data.Unit.ExecuteAsync((tx, ct) => data.Service.ReadPayableSourceAsync(data.Database, tx, id, ct));
        Assert.Equal(id, exact.Allocation["_id"].AsInt64);
        Assert.Equal(data.ReceiptId, exact.Receipt["_id"].AsInt64);
        Assert.Equal(data.Receipt["lotId"], exact.Receipt["lotId"]);
        Assert.Equal(1, exact.Allocation["packages"].AsInt32);
        Assert.Equal("11250.00", exact.Allocation["capitalizedAmount"].AsString);
        Assert.Equal(approved["journalId"], exact.Decision["journalId"]);
        Assert.Equal(before, await data.StateAsync());
        await data.NoPaymentOrReleaseAsync();
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", approved["journalId"])).SingleAsync();
        Assert.Equal(1L, (await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", journal["_id"]))).DeletedCount);
        try {
            Assert.Equal("procurement_stock_valuation_integrity_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                data.Unit.ExecuteAsync((tx, ct) => data.Service.ReadPayableSourceAsync(data.Database, tx, id, ct)))).Code);
        } finally { await data.Rows("journalEntries").InsertOneAsync(journal); }
        Assert.Equal(before, await data.StateAsync());
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceAliasTests
{
    [Theory]
    [InlineData("head-deleted")] [InlineData("foreign-key")]
    public async Task Approved_mapping_provenance_cannot_be_rewritten_by_missing_or_foreign_history(string fault)
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var (maker, reviewer, pool) = await Setup(data); var service = Service(data);
        var first = await Propose(service, maker, pool); await Approve(service, reviewer, first["proposalId"].AsInt64);
        if (fault == "head-deleted") await Rows(data, "stockSourceAliases").DeleteOneAsync(new BsonDocument());
        else
        {
            await Rows(data, "stockSourceAliases").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("physicalKey", "foreign-key"));
            await Rows(data, "stockSourceAliasProposals").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("physicalKey", "foreign-key"));
            await Rows(data, "auditEvents").UpdateOneAsync(new BsonDocument("operation", "stock.source.alias.propose"),
                Builders<BsonDocument>.Update.Set("record.physicalKey", "foreign-key"));
        }
        await Assert.ThrowsAsync<DomainValidationException>(() => Propose(service, maker, pool, "bad-next", fault == "head-deleted" ? 0 : 1, fault == "head-deleted"));
        Assert.Equal(1L, await Count(data, "stockSourceAliasProposals")); Assert.Equal(4L, await Count(data, "idempotencyRecords"));
    }
}

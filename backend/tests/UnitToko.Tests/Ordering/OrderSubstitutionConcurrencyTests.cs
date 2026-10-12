using System.Runtime.Versioning;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Inventory;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionConcurrencyTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Concurrent_explicit_consents_commit_one_atomic_remaining_release_and_replacement_reservation() {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false); await using var data = prepared.Data;
        var target = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
        var original = Assert.Single(target.Lines); Assert.Equal(12, original.RemainingPackages);
        var replacement = Assert.Single((await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId)).Items,
            item => item.Definition.SkuId.Value == prepared.ReplacementSku);
        var input = new OrderSubstitutionInput(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
            new(new(data.SkuId), 12, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version, replacement.Price.Id, replacement.Price.Version, 1)));
        var preview = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
        var proposed = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, preview.Review, GenuineFiniteDispatchTestData.Meta("substitution-race-proposal"));
        var review = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
        async Task<(string Key, BsonDocument? Value, Exception? Error)> Accept(string key) {
            try { return (key, await data.Orders.DecideSubstitutionAsync(data.Buyer, review.ProposalId.Value, review.Revision,
                OrderSubstitutionDecision.Accept, review.Review, GenuineFiniteDispatchTestData.Meta(key)), null); }
            catch (Exception error) { return (key, null, error); }
        }
        var results = await Task.WhenAll(Accept("substitution-race-first"), Accept("substitution-race-second"));
        var winner = Assert.Single(results, result => result.Value is not null);
        var loser = Assert.Single(results, result => result.Error is not null);
        Assert.True(loser.Error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, loser.Error?.ToString());
        Assert.Equal(1L, await data.Rows(S.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", "ordering.substitution")));
        var reservations = await data.Rows("reservations").Find(new BsonDocument("orderId", data.OrderId)).ToListAsync(); Assert.Equal(2, reservations.Count);
        var old = Assert.Single(reservations, row => row["allocation"]["skuId"] == data.SkuId);
        var current = Assert.Single(reservations, row => row["allocation"]["skuId"] == prepared.ReplacementSku);
        Assert.Equal(12L, old["released"].AsInt64); Assert.Equal(0L, old["remaining"].AsInt64); Assert.Equal(0L, old["dispatched"].AsInt64);
        Assert.Equal(1L, current["remaining"].AsInt64); Assert.Equal(0L, current["released"].AsInt64); Assert.Equal(0L, current["dispatched"].AsInt64);
        Assert.Equal(winner.Value, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, winner.Key, OrderSubstitutionDecision.Accept));
        Assert.Null(await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, loser.Key, OrderSubstitutionDecision.Accept));
        _ = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, ct));
    }
}

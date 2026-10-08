using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Operations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Operations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Operations;

[Collection("Mongo")]
public sealed class OperationalAnomalyMongoTests(MongoFixture fixture)
{
    [Fact]
    public async Task Fifty_one_genuine_order_sources_are_reachable_in_two_scoped_cursor_pages()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, stock: 200);
        var catalog = data.Prices.Catalog;
        var reviewer = await catalog.Base.AddActorAsync(7811,
            new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), Permissions.AnomalyRead, Permissions.OrderRead);
        // The synthetic admission fixture permits this finite volume. Every order/suborder is still produced by its real owner.
        await data.Rows("fixtureOrderApprovals").UpdateOneAsync(new BsonDocument("_id", 800L), Builders<BsonDocument>.Update.Set("creditLimit", new BsonDecimal128(100000000m)));
        var expected = new List<long>();
        for (var index = 0; index < 51; index++) {
            var preview = await data.Service.PreviewAsync(data.Buyer, data.Input);
            var submitted = await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("anomaly-page-order-" + index));
            expected.Add(Assert.Single(submitted["suborderIds"].AsBsonArray).AsInt64);
        }
        var unit = new MongoUnitOfWork(catalog.Client);
        var service = new OperationalAnomalyService(data.Database, unit, new(data.Database), catalog.Clock, new(data.Database, unit, catalog.Clock), data.Service);
        var first = await service.ChoicesAsync(reviewer, "order");
        Assert.Equal(50, first.Items.Count); Assert.Equal(first.Items[^1].Id, first.NextCursor);
        var second = await service.ChoicesAsync(reviewer, "order", first.NextCursor!.Value.Value);
        Assert.Single(second.Items); Assert.Null(second.NextCursor);
        Assert.Equal(expected.OrderDescending(), first.Items.Concat(second.Items).Select(item => item.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ChoicesAsync(data.Buyer, "order"));
    }

    [Fact]
    public async Task Versioned_quantity_review_is_scoped_durable_idempotent_and_has_no_business_effect()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var catalog = data.Prices.Catalog; var store = catalog.Base;
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var reviewer = await store.AddActorAsync(7801, scope, Permissions.AnomalyRead, Permissions.AnomalyReview, Permissions.AnomalyPolicyConfigure, Permissions.OrderRead);
        var reader = await store.AddActorAsync(7802, scope, Permissions.AnomalyRead, Permissions.OrderRead);
        var noSourceGrant = await store.AddActorAsync(7803, scope, Permissions.AnomalyRead, Permissions.AnomalyReview);
        var unit = new MongoUnitOfWork(catalog.Client);
        OperationalAnomalyService Service() => new(data.Database, unit, new(data.Database), catalog.Clock, new(data.Database, unit, catalog.Clock), data.Service);
        var preview = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var submitted = await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("anomaly-order"));
        var sourceId = Assert.Single(submitted["suborderIds"].AsBsonArray).AsInt64;
        var before = await BusinessAsync(data);
        var absent = await Service().EvaluateAsync(reviewer, "order", sourceId, CatalogPriceTestData.Meta("anomaly-absent"));
        Assert.Null(absent.PolicyVersion); Assert.Equal(2, absent.Signals.Count(signal => signal.State == "policy_needed"));
        Assert.Equal("unavailable", absent.Signals.Single(signal => signal.Category == "adjustment").State);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ReadAsync(noSourceGrant, absent.Id.Value));
        // Raw policy text and an approval reference are insufficient without an authorized immutable producer receipt.
        var head = await data.Rows(OperationalAnomalyRecords.Policies).Find(new BsonDocument("_id", "100:101:103")).SingleAsync();
        await data.Rows(OperationalAnomalyRecords.Policies).UpdateOneAsync(new BsonDocument("_id", head["_id"]),
            Builders<BsonDocument>.Update.Set("policy", OperationalAnomalyRecords.Policy(new AnomalyReviewPolicy("forged-metadata", "UNPROVEN", 1, null, null, null))));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().EvaluateAsync(reviewer, "order", sourceId, CatalogPriceTestData.Meta("anomaly-unproven")));
        await data.Rows(OperationalAnomalyRecords.Policies).ReplaceOneAsync(new BsonDocument("_id", head["_id"]), head);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ConfigurePolicyAsync(reader, "order", sourceId, 1,
            new("unauthorized", "SYNTHETIC", 1, null, null, null), CatalogPriceTestData.Meta("anomaly-policy-denied")));
        await Service().ConfigurePolicyAsync(reviewer, "order", sourceId, 1, new("synthetic-review-1", "SYNTHETIC-TEST-ONLY", 1, null, null, null), CatalogPriceTestData.Meta("anomaly-configure-one"));
        var recoveredPolicy = await Service().RecoverPolicyAsync(reviewer, "order", sourceId, "anomaly-configure-one");
        Assert.Equal("confirmed", recoveredPolicy.Status); Assert.Equal("synthetic-review-1", recoveredPolicy.PolicyVersion);
        var first = await Service().EvaluateAsync(reviewer, "order", sourceId, CatalogPriceTestData.Meta("anomaly-policy-one"));
        Assert.Equal("flagged", first.Signals.Single(signal => signal.Category == "quantity").State);
        var disposition = await Service().ReviewAsync(reviewer, first.Id.Value, "requires_action", CatalogPriceTestData.Meta("anomaly-review-one"));
        Assert.Equal(reviewer.UserId, disposition.Disposition!.ReviewerId.Value);
        Assert.Equal("requires_action", disposition.Disposition.Outcome);
        var retry = await Service().ReviewAsync(reviewer, first.Id.Value, "requires_action", CatalogPriceTestData.Meta("anomaly-review-one"));
        Assert.Equal(disposition.Disposition, retry.Disposition);
        Assert.Equal(1, await data.Rows(OperationalAnomalyRecords.Dispositions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(disposition.Disposition, (await Service().LatestAsync(reader, "order", sourceId))!.Disposition);
        var storedDisposition = await data.Rows(OperationalAnomalyRecords.Dispositions).Find(new BsonDocument("evaluationId", first.Id.Value)).SingleAsync();
        await data.Rows(OperationalAnomalyRecords.Dispositions).UpdateOneAsync(new BsonDocument("_id", storedDisposition["_id"]),
            Builders<BsonDocument>.Update.Set("reason", "A forged human reason"));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().ReadAsync(reviewer, first.Id.Value));
        await data.Rows(OperationalAnomalyRecords.Dispositions).ReplaceOneAsync(new BsonDocument("_id", storedDisposition["_id"]), storedDisposition);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ReviewAsync(reader, first.Id.Value, "acknowledged", CatalogPriceTestData.Meta("anomaly-denied")));
        await Service().ConfigurePolicyAsync(reviewer, "order", sourceId, 2, new("synthetic-review-2", "SYNTHETIC-TEST-ONLY", 3, null, null, null), CatalogPriceTestData.Meta("anomaly-configure-two"));
        Assert.False((await Service().ReadAsync(reviewer, first.Id.Value)).Current);
        var second = await Service().EvaluateAsync(reviewer, "order", sourceId, CatalogPriceTestData.Meta("anomaly-policy-two"));
        Assert.Equal("within_policy", second.Signals.Single(signal => signal.Category == "quantity").State);
        Assert.Equal("synthetic-review-2", second.PolicyVersion);
        Assert.Equal(before, await BusinessAsync(data));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ReadAsync(reviewer with { GrantVersion = 2 }, second.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ReviewAsync(reviewer with { AuthenticatedAt = catalog.Clock.UtcNow.AddMinutes(-6) }, second.Id.Value,
            "acknowledged", CatalogPriceTestData.Meta("anomaly-stale-factor")));
        // A changed immutable review payload cannot pass the committed command receipt.
        await data.Rows(OperationalAnomalyRecords.Evaluations).UpdateOneAsync(new BsonDocument("_id", second.Id.Value),
            Builders<BsonDocument>.Update.Set("signals.0.explanation", "Synthetic tamper"));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().ReadAsync(reviewer, second.Id.Value));
        // The source owner rejects corrupt order evidence before advisory results can be used.
        await data.Rows("orders").UpdateOneAsync(new BsonDocument("_id", submitted["orderId"]), Builders<BsonDocument>.Update.Set("snapshotHash", new string('0', 64)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().ReadAsync(reviewer, first.Id.Value));
        Assert.Equal(1, await data.Rows(OperationalAnomalyRecords.Dispositions).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Original_operation_recovery_requires_current_actor_scope_and_exact_owner_receipt_provenance()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var catalog = data.Prices.Catalog;
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var actor = await catalog.Base.AddActorAsync(7821, scope, Permissions.AnomalyRead, Permissions.AnomalyReview, Permissions.AnomalyPolicyConfigure, Permissions.OrderRead);
        var other = await catalog.Base.AddActorAsync(7822, scope, Permissions.AnomalyRead, Permissions.AnomalyReview, Permissions.AnomalyPolicyConfigure, Permissions.OrderRead);
        var denied = await catalog.Base.AddActorAsync(7823, scope, Permissions.AnomalyRead);
        var unit = new MongoUnitOfWork(catalog.Client);
        OperationalAnomalyService Service() => new(data.Database, unit, new(data.Database), catalog.Clock, new(data.Database, unit, catalog.Clock), data.Service);
        var preview = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var submitted = await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("recovery-order"));
        var sourceId = Assert.Single(submitted["suborderIds"].AsBsonArray).AsInt64;
        var before = await BusinessAsync(data);
        Assert.Equal("not_confirmed", (await Service().RecoverAsync(actor, "order", sourceId, "evaluate", "never-dispatched-before-policy")).Status);
        Assert.Equal(0, await data.Rows(OperationalAnomalyRecords.Policies).CountDocumentsAsync(new BsonDocument()));
        await Service().ConfigurePolicyAsync(actor, "order", sourceId, 1, new("synthetic-original", "SYNTHETIC-ONLY", 1, null, null, null), CatalogPriceTestData.Meta("original-policy"));
        var evaluation = await Service().EvaluateAsync(actor, "order", sourceId, CatalogPriceTestData.Meta("original-evaluation"));
        await Service().ReviewAsync(actor, evaluation.Id.Value, "requires_action", CatalogPriceTestData.Meta("original-review"));
        Assert.Equal("confirmed", (await Service().RecoverPolicyAsync(actor, "order", sourceId, "original-policy")).Status);
        foreach (var operation in new[] { "evaluate", "review" }) {
            var key = operation == "evaluate" ? "original-evaluation" : "original-review";
            long? expectedId = operation == "review" ? evaluation.Id.Value : null;
            var result = await Service().RecoverAsync(actor, "order", sourceId, operation, key, expectedId);
            Assert.Equal("confirmed", result.Status); Assert.Equal(evaluation.Id, result.Evaluation!.Id);
            Assert.Equal("requires_action", result.Evaluation.Disposition!.Outcome);
            Assert.Equal("not_confirmed", (await Service().RecoverAsync(other, "order", sourceId, operation, key, expectedId)).Status);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().RecoverAsync(denied, "order", sourceId, operation, key, expectedId));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().RecoverAsync(actor with { GrantVersion = 2 }, "order", sourceId, operation, key, expectedId));
        }
        Assert.Equal("not_confirmed", (await Service().RecoverPolicyAsync(other, "order", sourceId, "original-policy")).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service().RecoverPolicyAsync(denied, "order", sourceId, "original-policy"));
        var policyReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", actor.UserId }, { "operation", OperationalAnomalyRecords.ConfigureOperation }, { "key", "original-policy" } }).SingleAsync();
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", policyReceipt["_id"]));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().RecoverPolicyAsync(actor, "order", sourceId, "original-policy"));
        await data.Rows("idempotencyRecords").InsertOneAsync(policyReceipt);
        Assert.Equal("not_confirmed", (await Service().RecoverAsync(actor, "order", sourceId, "evaluate", "never-dispatched")).Status);
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().RecoverAsync(actor, "order", sourceId, "review", "original-review", evaluation.Id.Value + 1));
        // A committed owner without its original receipt must not be mistaken for an uncommitted request.
        foreach (var (operation, key) in new[] { (OperationalAnomalyRecords.EvaluateOperation, "original-evaluation"), (OperationalAnomalyRecords.ReviewOperation, "original-review") }) {
            var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "key", key } }).SingleAsync();
            await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
            await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().RecoverAsync(actor, "order", sourceId,
                operation == OperationalAnomalyRecords.EvaluateOperation ? "evaluate" : "review", key, operation == OperationalAnomalyRecords.ReviewOperation ? evaluation.Id.Value : null));
            await data.Rows("idempotencyRecords").InsertOneAsync(receipt);
        }
        var disposition = await data.Rows(OperationalAnomalyRecords.Dispositions).Find(new BsonDocument("evaluationId", evaluation.Id.Value)).SingleAsync();
        await data.Rows(OperationalAnomalyRecords.Dispositions).UpdateOneAsync(new BsonDocument("_id", disposition["_id"]), Builders<BsonDocument>.Update.Set("reason", "Forged recovered reason"));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().RecoverAsync(actor, "order", sourceId, "review", "original-review", evaluation.Id.Value));
        await data.Rows(OperationalAnomalyRecords.Dispositions).ReplaceOneAsync(new BsonDocument("_id", disposition["_id"]), disposition);
        var reviewReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", actor.UserId }, { "operation", OperationalAnomalyRecords.ReviewOperation }, { "key", "original-review" } }).SingleAsync();
        await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("_id", reviewReceipt["_id"]), Builders<BsonDocument>.Update.Set("result.dispositionHash", new string('0', 64)));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => Service().RecoverAsync(actor, "order", sourceId, "review", "original-review", evaluation.Id.Value));
        await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", reviewReceipt["_id"]), reviewReceipt);
        Assert.Equal("confirmed", (await Service().RecoverAsync(actor, "order", sourceId, "review", "original-review", evaluation.Id.Value)).Status);
        Assert.Equal(1, await data.Rows(OperationalAnomalyRecords.Evaluations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(OperationalAnomalyRecords.Dispositions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await BusinessAsync(data));
    }
    private static async Task<BsonDocument> BusinessAsync(OrderSubmissionTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "reservations", "stockBalances", "movements", "journalEntries", "outboxEvents", "authorizationGrants", "memberships" }) {
            var rows = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        return result;
    }
}

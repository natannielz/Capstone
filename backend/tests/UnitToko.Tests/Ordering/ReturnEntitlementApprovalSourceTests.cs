using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnEntitlementApprovalSourceTests(MongoFixture fixture)
{
    private static async Task<ActorContext> Reader(ReturnEntitlementTests.Data data) {
        var actor = await data.Payment.Invoice.Order.Prices.Catalog.ActorAsync(99880, CatalogAuthoringTestData.Shop, Permissions.CaseRead, Permissions.InvoiceRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, actor.UserId); return actor;
    }
    private static Task<FinancialEntitlementRequestSource> Source(ReturnEntitlementTests.Data data, ActorContext actor, bool newEffect, bool commandFence = false) =>
        new AuthorizationStore(data.Database, new(data.Payment.Invoice.Order.Prices.Catalog.Client), data.Clock).ReadAsync(actor, async (authority, tx, ct) => {
            var source = await data.Service.ApprovalSourceAsync(actor, authority, tx, data.CaseId, data.ReturnId, data.Action, newEffect, ct);
            if (commandFence) await source.FenceNewAsync(ct); else await source.FenceReadAsync(ct);
            source.RequireCurrent(); return source;
        }, CancellationToken.None);

    [Fact]
    public async Task Actual_approval_source_uses_only_case_and_invoice_read_and_fences_the_original_request_without_changing_its_pool_or_money() {
        await using var data = await ReturnEntitlementTests.Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); await data.RequestAsync(preview, ReturnEntitlementTests.Data.Meta("approval-source-original"));
        var reader = await Reader(data); var before = await data.BusinessAsync();
        var request = await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync(); var pool = await data.Rows(E.Pools).Find(new BsonDocument()).SingleAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(reader, data.CaseId, data.ReturnId, data.Action));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", ReturnEntitlementTests.Data.FinancialGrantId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.CreditRequest));
        var invoiceFence = await data.Rows("invoiceCaseReturnFences").Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync();
        var historic = await Source(data, reader, false); Assert.Equal(request, historic.Request); Assert.Equal(data.Actor.UserId, historic.Request["actorId"].AsInt64);
        Assert.Equal(1L, historic.Pool["version"].AsInt64); Assert.Equal(1000.02m, historic.Pool["lines"][0]["net"].AsDecimal);
        Assert.Equal(invoiceFence, await data.Rows("invoiceCaseReturnFences").Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync());
        var current = await Source(data, reader, true, true); Assert.Equal(request, current.Request);
        Assert.Equal(invoiceFence["fence"].AsInt64 + 1, (await data.Rows("invoiceCaseReturnFences").Find(new BsonDocument("_id", data.Payment.Issued.Id.Value)).SingleAsync())["fence"].AsInt64);
        Assert.Equal(pool, await data.Rows(E.Pools).Find(new BsonDocument()).SingleAsync()); Assert.Equal(request, await data.Rows(E.Requests).Find(new BsonDocument("_id", data.ReturnId)).SingleAsync());
        await data.AssertBusinessAsync(before);
    }

    [Fact]
    public async Task Source_rejects_foreign_transaction_and_history_gaps_and_new_effect_requires_current_actual_maker_person() {
        await using var data = await ReturnEntitlementTests.Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); await data.RequestAsync(preview, ReturnEntitlementTests.Data.Meta("approval-source-protected"));
        var reader = await Reader(data); var catalog = data.Payment.Invoice.Order.Prices.Catalog;
        var authorization = new AuthorizationStore(data.Database, new(catalog.Client), data.Clock);
        await authorization.ReadAsync(reader, async (authority, tx, ct) => {
            var foreignClient = new MongoClient(catalog.Client.Settings);
            using var foreign = await foreignClient.StartSessionAsync(cancellationToken: ct); foreign.StartTransaction();
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ApprovalSourceAsync(reader, authority, foreign, data.CaseId, data.ReturnId, data.Action, true, ct));
            await foreign.AbortTransactionAsync(ct);
            var historic = await data.Service.ApprovalSourceAsync(reader, authority, tx, data.CaseId, data.ReturnId, data.Action, false, ct);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => historic.FenceNewAsync(ct)); return true;
        }, CancellationToken.None);
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Actor.UserId), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime));
        await Source(data, reader, false); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Source(data, reader, true));
        Assert.Equal(1, (await data.Rows(E.Pools).DeleteOneAsync(new BsonDocument("_id", data.Payment.Issued.Id.Value))).DeletedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Source(data, reader, false)); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Source(data, reader, true));
    }
}

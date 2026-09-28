using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingAuthorizationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Revocation_during_outer_target_resolution_prevents_accounting_and_business_effects()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var admin = await data.Access.AddActorAsync(1001, new(GrantScopeKind.BuyingOrganization, 10), Permissions.AccessRevoke);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var work = data.Access.Store.ExecuteAuthorizedAsync(data.Actor, "fixture.receipt", Permissions.OrderSubmit, new("revoked", "Synthetic request", "test"), new { Id = 42 }, async (session, token) =>
        {
            var row = await data.DB.GetCollection<BsonDocument>("orders").Find(session, new BsonDocument("_id", 42L)).SingleAsync(token);
            if (Interlocked.Increment(ref calls) == 1)
            {
                read.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return new(AuthorizationBson.Resource(row));
        }, async (session, token) =>
        {
            await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(session, new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Inc("effects", 1), cancellationToken: token);
            var posted = await data.Writer.PostAsync(session, data.Actor, AccountingData.Source, 1, AccountingData.Metadata, token);
            return new BsonDocument("id", posted.Id);
        });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.Access.Administration.RevokeGrantAsync(admin, data.Actor.UserId, 1, new("revoke", "Synthetic revocation", "test"));
        }
        finally
        {
            resume.TrySetResult();
        }

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => work);
        Assert.False((await data.Order()).Contains("effects"));
        Assert.False((await data.Order()).Contains("accountingFence"));
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.Equal(0, await data.DB.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "revoked")));
    }

    [Fact]
    public async Task Owning_wrapper_final_session_fence_rolls_back_after_session_expires_during_source_work()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var actor = data.Actor;
        await data.DB.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.DB, data.Uow, data.Access.Clock, new(actor.ProviderIssuer, "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true, "fixture", data.Access.Clock.UtcNow, data.Access.Clock.UtcNow, "otp", "fixture");
        var issued = await data.Uow.ExecuteAsync((tx, token) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "fixture", token));
        actor = actor with
        {
            Session = issued.Session.Reference
        };
        data.Reader.BeforeFence = () =>
        {
            data.Access.Clock.UtcNow += TimeSpan.FromMinutes(31);
            return Task.CompletedTask;
        };
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CommandAsync("expired", actor));
        Assert.Equal("stale_or_inactive_session", failure.Code);
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.Equal(0, await data.Count("idempotencyRecords"));
        Assert.False((await data.Order()).Contains("effects"));
    }

    [Fact]
    public async Task Actual_uncertain_commit_recovers_original_authorized_receipt_and_one_cashless_journal()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var settings = fixture.Client.Settings.Clone();
        settings.ApplicationName = "accounting-commit-" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var store = new AuthorizationStore(db, new(client), data.Access.Clock);
        var writer = new PostJournal(db, data.Access.Clock, [new FixtureSourceReader(db)]);
        Task<BsonDocument> Command() => store.ExecuteAuthorizedAsync(data.Actor, "fixture.receipt", Permissions.OrderSubmit, new("uncertain", AccountingData.Metadata.Reason, AccountingData.Metadata.CorrelationId), new { Source = AccountingData.Source }, async (session, token) => new(AuthorizationBson.Resource(await db.GetCollection<BsonDocument>("orders").Find(session, new BsonDocument("_id", 42L)).SingleAsync(token))), async (session, token) =>
        {
            await db.GetCollection<BsonDocument>("orders").UpdateOneAsync(session, new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Inc("effects", 1), cancellationToken: token);
            var result = await writer.PostAsync(session, data.Actor, AccountingData.Source, 1, AccountingData.Metadata, token);
            return new BsonDocument("journalId", result.Id);
        });
        try
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" }, { "data", new BsonDocument { { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "commitTransaction" } }, { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "synthetic uncertain accounting commit" } } }, { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } } } } });
            await Assert.ThrowsAsync<UncertainCommitException>(Command);
        }
        finally
        {
            await fixture.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
        }

        var stored = await db.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "uncertain")).SingleAsync();
        Assert.Equal(stored["result"], await Command());
        Assert.Equal(1, await data.Count("journalEntries"));
        Assert.Equal(1, (await data.Order())["effects"].AsInt32);
        Assert.Equal(1, await data.Count("idempotencyRecords"));
    }
}

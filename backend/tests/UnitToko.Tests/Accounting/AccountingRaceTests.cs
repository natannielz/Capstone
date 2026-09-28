using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingRaceTests(MongoFixture fixture)
{
    [Fact]
    public async Task Close_wins_and_preexisting_post_snapshot_retries_into_closed_guard()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await AccountingPeriodTests.SeedApprovalAsync(data, "2026-09");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        data.Reader.BeforeFence = async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                read.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        var post = data.CommandAsync("racing-post");
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.Uow.ExecuteAsync(async (session, token) =>
            {
                await new AccountingPeriodStore(data.DB, data.Access.Clock).CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-09", AccountingData.Metadata, token);
                return 0;
            });
        }
        finally
        {
            resume.TrySetResult();
        }

        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => post);
        Assert.Equal("accounting_period_closed", failure.Code);
        Assert.True(calls >= 2);
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.False((await data.Order()).Contains("effects"));
        Assert.Equal(0, await data.Count("idempotencyRecords"));
    }

    [Fact]
    public async Task Post_wins_and_stale_close_retries_into_invalidated_approval()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await AccountingPeriodTests.SeedApprovalAsync(data, "2026-09");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var close = data.Uow.ExecuteAsync(async (session, token) =>
        {
            var periods = new AccountingPeriodStore(data.DB, data.Access.Clock);
            await periods.CaptureAsync(session, AccountingData.Book, "2026-09", token);
            if (Interlocked.Increment(ref calls) == 1)
            {
                read.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await periods.CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-09", AccountingData.Metadata, token);
            return 0;
        });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.CommandAsync("winning-post");
        }
        finally
        {
            resume.TrySetResult();
        }

        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => close);
        Assert.Equal("accounting_approval_stale", failure.Code);
        Assert.True(calls >= 2);
        Assert.Equal(1, await data.Count("journalEntries"));
        Assert.Equal("open", (await AccountingPeriodTests.Period(data, "2026-09"))["state"].AsString);
    }

    [Fact]
    public async Task Source_revision_write_fence_rejects_changed_source_after_snapshot_read()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        data.Reader.BeforeFence = async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                read.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        var post = data.Uow.ExecuteAsync(async (session, token) =>
        {
            await data.DB.GetCollection<BsonDocument>("goodsReceipts").InsertOneAsync(session, new BsonDocument { { "_id", 9L }, { "schemaVersion", 1 } }, cancellationToken: token);
            return await data.Writer.PostAsync(session, data.Actor, AccountingData.Source, 1, AccountingData.Metadata, token);
        });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("version", 2L).Set("amount", new BsonDecimal128(200m)));
        }
        finally
        {
            resume.TrySetResult();
        }

        var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => post);
        Assert.Equal("source_revision_conflict", failure.Code);
        Assert.True(calls >= 2);
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.Equal(0, await data.Count("goodsReceipts"));
    }

    [Fact]
    public async Task Different_actors_and_command_keys_still_post_one_source_once()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var second = await data.Access.AddActorAsync(1001, new(GrantScopeKind.BuyingOrganization, 10), Permissions.OrderSubmit, Permissions.OrganizationWide);
        async Task<string> Try(string key, UnitToko.Modules.Identity.Contracts.ActorContext actor)
        {
            try
            {
                await data.CommandAsync(key, actor);
                return "posted";
            }
            catch (PersistenceConflictException exception)
            {
                return exception.Code;
            }
        }

        var results = await Task.WhenAll(Try("one", data.Actor), Try("two", second));
        Assert.Single(results, value => value == "posted");
        Assert.Single(results, value => value == "journal_source_duplicate");
        Assert.Equal(1, await data.Count("journalEntries"));
        Assert.Equal(1, await data.Count("idempotencyRecords"));
        Assert.Equal(1, (await data.Order())["effects"].AsInt32);
    }

    [Fact]
    public async Task Snapshot_uses_multiple_bounded_cursor_batches_and_stable_business_order()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var documents = Enumerable.Range(1, 70).Reverse().Select(index =>
        {
            var draft = JournalDraft.Create(new(new("fixture", index, "receipt", 1), 1, AccountingData.Book, new(100, 101, 103, 102, 10, 11), new(2026, 9, 28), [new("inventory", new(index), new(0m)), new("payable", new(0m), new(index))]), new(20, 1, 1, [new("inventory", 501, "INV", true), new("payable", 502, "AP", true)]));
            return AccountingBson.Journal(index, 1, draft, data.Actor, AccountingData.Metadata, data.Access.Clock.UtcNow);
        }).ToArray();
        await data.DB.GetCollection<BsonDocument>("journalEntries").InsertManyAsync(documents);
        var batches = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "getMore" && command.Command.GetValue("collection", "") == "journalEntries")
                Interlocked.Increment(ref batches);
        });
        var client = new MongoClient(settings);
        var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var periods = new AccountingPeriodStore(db, data.Access.Clock);
        var snapshot = await new MongoUnitOfWork(client).ExecuteAsync((session, token) => periods.CaptureAsync(session, AccountingData.Book, "2026-09", token));
        Assert.True(batches > 0, "The cumulative journal cursor must use a bounded batch below this 70-row fixture");
        Assert.Equal(2485m, snapshot.Debit);
        Assert.Equal(70, snapshot.JournalCount);
        await data.DB.GetCollection<BsonDocument>("journalEntries").DeleteManyAsync(new BsonDocument());
        await data.DB.GetCollection<BsonDocument>("journalEntries").InsertManyAsync(documents.Reverse());
        var repeated = await new MongoUnitOfWork(client).ExecuteAsync((session, token) => periods.CaptureAsync(session, AccountingData.Book, "2026-09", token));
        Assert.Equal(snapshot, repeated);
    }
}

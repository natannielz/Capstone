using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingPostingTests(MongoFixture fixture)
{
    [Fact]
    public async Task Real_authorized_caller_commits_business_journal_audit_outbox_and_replays_once()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var result = await data.CommandAsync("first");
        Assert.Equal(result, await data.CommandAsync("first"));
        Assert.Equal(1, await data.Count("journalEntries"));
        Assert.Equal(1, (await data.Order())["effects"].AsInt32);
        var journal = await data.DB.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(100m, journal["total"].AsDecimal);
        Assert.Equal(501L, journal["lines"][0]["accountId"].AsInt64);
        Assert.Equal(20L, journal["chartId"].AsInt64);
        Assert.Equal(data.Actor.UserId, journal["actorId"].AsInt64);
        Assert.Equal(4, await data.Count("accountingAuditEvents"));
        Assert.Equal(4, await data.Count("outboxEvents"));
        Assert.Equal(1, await data.Count("idempotencyRecords"));
        Assert.Equal(1L, (await data.DB.GetCollection<BsonDocument>("periods").Find(new BsonDocument("_id", 3L)).SingleAsync())["dataRevision"].AsInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_source_aborts_even_when_caller_catches_it(bool changed)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.CommandAsync("original");
        if (changed)
            await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("amount", new BsonDecimal128(200m)));
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(session, new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("caughtEffect", true));
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Writer.PostAsync(session, data.Actor, AccountingData.Source, 1, AccountingData.Metadata));
        Assert.Equal(changed ? "journal_source_conflict" : "journal_source_duplicate", error.Code);
        Assert.False(session.IsInTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitTransactionAsync());
        Assert.False((await data.Order()).Contains("caughtEffect"));
        Assert.Equal(1, await data.Count("journalEntries"));
    }

    [Theory]
    [InlineData("chart")]
    [InlineData("wrong_chart_book")]
    [InlineData("inactive_chart")]
    [InlineData("book")]
    [InlineData("period")]
    [InlineData("account")]
    public async Task Missing_or_incompatible_configuration_rolls_back_entire_authorized_operation(string failure)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        if (failure == "chart")
            await data.DB.GetCollection<BsonDocument>("accountingChartVersions").DeleteOneAsync(new BsonDocument("_id", 20L));
        if (failure == "wrong_chart_book")
            await data.DB.GetCollection<BsonDocument>("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Set("bookId", 999L));
        if (failure == "inactive_chart")
            await data.DB.GetCollection<BsonDocument>("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Set("active", false));
        if (failure == "book")
            await data.DB.GetCollection<BsonDocument>("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("active", false));
        if (failure == "period")
            await data.DB.GetCollection<BsonDocument>("periods").DeleteOneAsync(new BsonDocument("_id", 1L));
        if (failure == "account")
            await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("mapping", "unknown"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.CommandAsync("failure"));
        Assert.False((await data.Order()).Contains("effects"));
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.Equal(0, await data.Count("idempotencyRecords"));
        Assert.Equal(0, await data.Count("outboxEvents"));
    }

    [Theory]
    [InlineData("accountingAuditEvents")]
    [InlineData("outboxEvents")]
    public async Task Required_history_delivery_failure_aborts_all_effects(string collection)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.DB.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", collection }, { "validator", new BsonDocument("neverAllowed", new BsonDocument("$exists", true)) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.CommandAsync("audit-failure"));
        Assert.False((await data.Order()).Contains("effects"));
        Assert.Equal(0, await data.Count("journalEntries"));
        Assert.Equal(0, await data.Count("idempotencyRecords"));
        Assert.Equal(0, await data.Count("accountingPeriodHistory"));
    }

    [Fact]
    public async Task Inactive_session_and_unknown_namespace_are_rejected()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        using var session = await fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Writer.PostAsync(session, data.Actor, AccountingData.Source, 1, AccountingData.Metadata));
        session.StartTransaction();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Writer.PostAsync(session, data.Actor, new("unknown", 42, "receipt", 1), 1, AccountingData.Metadata));
        Assert.False(session.IsInTransaction);
    }

    [Fact]
    public async Task Exact_reversal_preserves_original_chart_and_denies_second_reversal()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var result = await data.CommandAsync("first");
        var original = await data.DB.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument()).SingleAsync();
        var chart = AccountingTestDocuments.Chart();
        chart["_id"] = 21L;
        chart["version"] = 2L;
        chart["mappings"][0]["accountId"] = 601L;
        await data.DB.GetCollection<BsonDocument>("accountingChartVersions").InsertOneAsync(chart);
        await data.DB.GetCollection<BsonDocument>("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("chartVersionId", 21L).Inc("version", 1L).Inc("fence", 1L));
        await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("originalJournalId", result["journalId"]).Set("date", "2026-10-01"));
        var reversal = await data.Uow.ExecuteAsync((session, token) => data.Writer.ReverseAsync(session, data.Actor, new("fixture", 42, "reversal", 1), 1, AccountingData.Metadata, token));
        var saved = await data.DB.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument("_id", reversal.Id)).SingleAsync();
        Assert.Equal(501L, saved["lines"][0]["accountId"].AsInt64);
        Assert.Equal(100m, saved["lines"][0]["credit"].AsDecimal);
        Assert.Equal(20L, saved["chartId"].AsInt64);
        Assert.Equal(original, await data.DB.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument("_id", original["_id"])).SingleAsync());
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Uow.ExecuteAsync((session, token) => data.Writer.ReverseAsync(session, data.Actor, new("fixture", 42, "reversal", 2), 1, AccountingData.Metadata, token)));
        Assert.Equal("journal_already_reversed", error.Code);
        Assert.Equal(2, await data.Count("journalEntries"));
    }
}

internal sealed class AccountingData : IAsyncDisposable
{
    internal StoreTestData Access { get; }
    internal IMongoDatabase DB => Access.Database;
    internal ActorContext Actor { get; private set; } = null !;
    internal FixtureSourceReader Reader { get; }
    internal PostJournal Writer { get; }
    internal MongoUnitOfWork Uow { get; }

    internal static readonly OperationalBookKey Book = new(100, 101, 103, "IDR");
    internal static readonly JournalSource Source = new("fixture", 42, "receipt", 1);
    internal static readonly AccountingWriteMetadata Metadata = new("Synthetic accounting composition", "accounting-test");
    private AccountingData(StoreTestData data, MongoFixture fixture)
    {
        Access = data;
        Reader = new(data.Database);
        Writer = new(DB, data.Clock, [Reader]);
        Uow = new(fixture.Client);
    }

    internal static async Task<AccountingData> CreateAsync(MongoFixture fixture)
    {
        var data = new AccountingData(await StoreTestData.CreateAsync(fixture), fixture);
        data.Actor = await data.Access.AddActorAsync(1000, new(GrantScopeKind.BuyingOrganization, 10), Permissions.OrderSubmit, Permissions.OrganizationWide);
        await data.Access.AddResourceAsync(42, 10, 11, data.Actor.UserId);
        await data.DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("amount", new BsonDecimal128(100m)).Set("date", "2026-09-28").Set("mapping", "inventory"));
        await AccountingTestDocuments.ConfigureAsync(data.DB);
        return data;
    }

    internal Task<BsonDocument> CommandAsync(string key, ActorContext? actor = null) => Access.Store.ExecuteAuthorizedAsync(actor ?? Actor, "fixture.receipt", Permissions.OrderSubmit, new(key, Metadata.Reason, Metadata.CorrelationId), new { Source }, async (session, token) => new(AuthorizationBson.Resource(await DB.GetCollection<BsonDocument>("orders").Find(session, new BsonDocument("_id", 42L)).SingleAsync(token))), async (session, token) =>
    {
        await DB.GetCollection<BsonDocument>("orders").UpdateOneAsync(session, new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Inc("effects", 1), cancellationToken: token);
        var receipt = await Writer.PostAsync(session, actor ?? Actor, Source, 1, Metadata, token);
        await Access.Store.FenceAsync(actor ?? Actor, [], session, token);
        return new BsonDocument("journalId", receipt.Id);
    });
    internal Task<long> Count(string name) => DB.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument());
    internal Task<BsonDocument> Order() => DB.GetCollection<BsonDocument>("orders").Find(new BsonDocument("_id", 42L)).SingleAsync();
    public ValueTask DisposeAsync() => Access.DisposeAsync();
}

internal sealed class FixtureSourceReader(IMongoDatabase database) : IAccountingSourceReader
{
    public string Namespace => "fixture";
    public IMongoDatabase Database => database;
    internal Func<Task>? BeforeFence { get; set; }

    private async Task<BsonDocument> LoadAsync(IClientSessionHandle session, JournalSource source, long expected, CancellationToken token)
    {
        var rows = database.GetCollection<BsonDocument>("orders");
        var row = await rows.Find(session, new BsonDocument("_id", source.DocumentId)).SingleAsync(token);
        if (BeforeFence is not null)
            await BeforeFence();
        var result = await rows.UpdateOneAsync(session, new BsonDocument { { "_id", source.DocumentId }, { "version", expected } }, Builders<BsonDocument>.Update.Inc("accountingFence", 1L), cancellationToken: token);
        if (result.ModifiedCount != 1 || row["version"] != expected)
            throw new PersistenceConflictException("source_revision_conflict", "Source changed");
        return row;
    }

    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken token)
    {
        var row = await LoadAsync(session, source, expectedRevision, token);
        var amount = new Money(row["amount"].AsDecimal);
        return new(source, row["version"].AsInt64, AccountingData.Book, new(100, 101, 103, 102, 10, 11), DateOnly.ParseExact(row["date"].AsString, "yyyy-MM-dd"), [new(row["mapping"].AsString, amount, new(0m)), new("payable", new(0m), amount)]);
    }

    public async Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken token)
    {
        var row = await LoadAsync(session, source, expectedRevision, token);
        return new(source, row["version"].AsInt64, AccountingData.Book, new(100, 101, 103, 102, 10, 11), DateOnly.ParseExact(row["date"].AsString, "yyyy-MM-dd"), row["originalJournalId"].AsInt64);
    }
}

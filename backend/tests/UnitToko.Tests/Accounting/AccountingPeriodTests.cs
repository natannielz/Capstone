using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingPeriodTests(MongoFixture fixture)
{
    [Fact]
    public async Task Closed_through_blocks_earlier_month_and_current_month()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.CommandAsync("first");
        await SeedApprovalAsync(data, "2026-10");
        await data.Uow.ExecuteAsync(async (session, token) =>
        {
            await new AccountingPeriodStore(data.DB, data.Access.Clock).CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-10", AccountingData.Metadata, token);
            return 0;
        });
        foreach (var date in new[]
        {
            "2026-09-30",
            "2026-10-31"
        }

        )
        {
            await SetDateAsync(data, date);
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Uow.ExecuteAsync((session, token) => data.Writer.PostAsync(session, data.Actor, new("fixture", 42, "late", 1), 1, AccountingData.Metadata, token)));
            Assert.Equal("accounting_period_closed", error.Code);
        }

        Assert.Equal("2026-10", (await data.DB.GetCollection<BsonDocument>("accountingBooks").Find(new BsonDocument("_id", 1L)).SingleAsync())["closedThrough"].AsString);
    }

    [Fact]
    public async Task Backdate_invalidates_later_approvals_but_future_post_preserves_earlier_snapshot()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        var september = await SeedApprovalAsync(data, "2026-09");
        var october = await SeedApprovalAsync(data, "2026-10");
        await SetDateAsync(data, "2026-11-01");
        await data.CommandAsync("future");
        Assert.Equal("approved", (await Period(data, "2026-09"))["state"].AsString);
        Assert.Equal(september.Hash, (await Period(data, "2026-09"))["snapshotHash"].AsString);
        await SetDateAsync(data, "2026-09-20");
        await data.Uow.ExecuteAsync((session, token) => data.Writer.PostAsync(session, data.Actor, new("fixture", 42, "backdate", 1), 1, AccountingData.Metadata, token));
        Assert.Equal("open", (await Period(data, "2026-09"))["state"].AsString);
        Assert.Equal("open", (await Period(data, "2026-10"))["state"].AsString);
        Assert.True((await Period(data, "2026-10"))["dataRevision"].AsInt64 > october.DataRevision);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Uow.ExecuteAsync(async (session, token) =>
        {
            await new AccountingPeriodStore(data.DB, data.Access.Clock).CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-10", AccountingData.Metadata, token);
            return 0;
        }));
    }

    [Fact]
    public async Task Unapproved_or_tampered_history_never_closes()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        async Task Close() => await data.Uow.ExecuteAsync(async (session, token) =>
        {
            await new AccountingPeriodStore(data.DB, data.Access.Clock).CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-09", AccountingData.Metadata, token);
            return 0;
        });
        await Assert.ThrowsAsync<PersistenceConflictException>(Close);
        await SeedApprovalAsync(data, "2026-09");
        await data.DB.GetCollection<BsonDocument>("accountingPeriodHistory").UpdateOneAsync(new BsonDocument("stage", "approved"), Builders<BsonDocument>.Update.Set("actorId", 900L));
        await Assert.ThrowsAsync<PersistenceConflictException>(Close);
        Assert.Equal("approved", (await Period(data, "2026-09"))["state"].AsString);
    }

    [Fact]
    public async Task Cumulative_snapshot_allows_two_valid_large_journals_above_one_journal_cap()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.DB.GetCollection<BsonDocument>("fixtureBusinessRecords").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("amount", new BsonDecimal128(999999999999999.99m)));
        await data.CommandAsync("large");
        await data.Uow.ExecuteAsync((session, token) => data.Writer.PostAsync(session, data.Actor, new("fixture", 42, "another", 1), 1, AccountingData.Metadata, token));
        var snapshot = await data.Uow.ExecuteAsync((session, token) => new AccountingPeriodStore(data.DB, data.Access.Clock).CaptureAsync(session, AccountingData.Book, "2026-09", token));
        Assert.Equal(1999999999999999.98m, snapshot.Debit);
        Assert.Equal(snapshot.Debit, snapshot.Credit);
        Assert.Equal(2, snapshot.JournalCount);
    }

    internal static Task<BsonDocument> Period(AccountingData data, string month) => data.DB.GetCollection<BsonDocument>("periods").Find(new BsonDocument("month", month)).SingleAsync();
    internal static Task SetDateAsync(AccountingData data, string date) => data.DB.GetCollection<BsonDocument>("fixtureBusinessRecords").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update.Set("date", date));
    // Explicit synthetic approved-stage fixture only: this is not an implemented approval/reconciliation workflow.
    internal static async Task<AccountingStorageSnapshot> SeedApprovalAsync(AccountingData data, string month)
    {
        var snapshot = await data.Uow.ExecuteAsync((session, token) => new AccountingPeriodStore(data.DB, data.Access.Clock).CaptureAsync(session, AccountingData.Book, month, token));
        var ids = new IdAllocator(data.DB);
        var submit = (await ids.NextAsync("accountingPeriodHistory")).Value;
        var approve = (await ids.NextAsync("accountingPeriodHistory")).Value;
        var period = await Period(data, month);
        var version = period["version"].AsInt64;
        BsonDocument History(long id, long v, string stage, long actor, BsonValue previous) => new()
        {
            {
                "_id",
                id
            },
            {
                "schemaVersion",
                1
            },
            {
                "bookId",
                1L
            },
            {
                "periodId",
                snapshot.PeriodId
            },
            {
                "month",
                month
            },
            {
                "version",
                v
            },
            {
                "dataRevision",
                snapshot.DataRevision
            },
            {
                "stage",
                stage
            },
            {
                "previousHistoryId",
                previous
            },
            {
                "snapshotHash",
                snapshot.Hash
            },
            {
                "actorId",
                actor
            },
            {
                "actorGrantVersion",
                1L
            },
            {
                "createdAt",
                data.Access.Clock.UtcNow.UtcDateTime
            },
            {
                "reason",
                "Synthetic approval fixture"
            },
            {
                "correlationId",
                "period-test"
            }
        };
        await data.DB.GetCollection<BsonDocument>("accountingPeriodHistory").InsertManyAsync([History(submit, version + 1, "submitted", 900, period["lastHistoryId"]), History(approve, version + 2, "approved", 901, new BsonInt64(submit))]);
        await data.DB.GetCollection<BsonDocument>("periods").UpdateOneAsync(new BsonDocument("_id", snapshot.PeriodId), Builders<BsonDocument>.Update.Set("version", version + 2).Set("state", "approved").Set("approvedRevision", snapshot.DataRevision).Set("snapshotHash", snapshot.Hash).Set("lastHistoryId", approve));
        return snapshot;
    }
}

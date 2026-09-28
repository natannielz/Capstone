using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Clean_install_is_strict_and_never_seeds_policy()
    {
        var db = fixture.Client.GetDatabase("ut_accounting_schema_" + Guid.NewGuid().ToString("N"));
        try
        {
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            foreach (var name in new[]
            {
                "accountingBooks",
                "accountingChartVersions",
                "journalEntries",
                "periods",
                "accountingPeriodHistory",
                "accountingAuditEvents"
            }

            )
            {
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
                await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(new BsonDocument { { "_id", 10L }, { "schemaVersion", 1 } }));
            }

            await AccountingTestDocuments.ConfigureAsync(db);
            await db.GetCollection<BsonDocument>("journalEntries").InsertOneAsync(AccountingTestDocuments.Journal());
            var saved = await db.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument("_id", 1L)).SingleAsync();
            foreach (var mutation in new Action<BsonDocument>[]
            {
                row => row["lines"][0]["debit"] = new BsonDecimal128(1.001m),
                row => row["lines"][0]["debit"] = 100.0,
                row => row["lines"][0]["credit"] = new BsonDecimal128(1m),
                row => row["lines"][1]["credit"] = new BsonDecimal128(99m),
                row => row["month"] = "2026-10",
                row => row["businessDate"] = "2026-02-30",
                row => row["sourceEvent"] = "wrong",
                row => row["dimensions"]["shopId"] = 999L,
                row => row["lines"][0]["hidden"] = true,
                row => row["total"] = new BsonDecimal128(100.000m)
            }

            )
            {
                var bad = saved.DeepClone().AsBsonDocument;
                mutation(bad);
                await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("journalEntries").ReplaceOneAsync(new BsonDocument("_id", 1L), bad));
            }
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Malformed_legacy_rows_fail_before_any_accounting_target_ddl()
    {
        var db = fixture.Client.GetDatabase("ut_accounting_preflight_" + Guid.NewGuid().ToString("N"));
        try
        {
            await AccountingTestDocuments.ApplyV11Async(db);
            await db.GetCollection<BsonDocument>("journalEntries").InsertOneAsync(new BsonDocument { { "schemaVersion", 1 }, { "sourceEvent", "old" } });
            var before = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "journalEntries") })).SingleAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
            var after = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "journalEntries") })).SingleAsync();
            Assert.Equal(before, after);
            Assert.DoesNotContain("accountingBooks", await (await db.ListCollectionNamesAsync()).ToListAsync());
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
        }
    }
}

internal static class AccountingTestDocuments
{
    internal static async Task ApplyV11Async(IMongoDatabase db)
    {
        var clock = new SystemClock();
        var gate = new MigrationGate(db, clock, 11);
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db), new IdentityMigration(db), new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db), new PersonAuthorityMigration(db), new PersonGovernanceMigration(db), new PersonReviewPreviewMigration(db), new CatalogMigration(db)];
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, default);
            if (lease is null)
                continue;
            await step.ApplyAsync(lease, default);
            await lease.CompleteAsync(default);
        }
    }

    internal static BsonDocument Key() => new()
    {
        {
            "organizationId",
            100L
        },
        {
            "shopId",
            101L
        },
        {
            "channelId",
            103L
        },
        {
            "currency",
            "IDR"
        }
    };
    internal static BsonDocument Dimensions() => new()
    {
        {
            "organizationId",
            100L
        },
        {
            "shopId",
            101L
        },
        {
            "channelId",
            103L
        },
        {
            "warehouseId",
            102L
        },
        {
            "buyingOrganizationId",
            10L
        },
        {
            "branchId",
            11L
        }
    };
    internal static BsonDocument Book() => new()
    {
        {
            "_id",
            1L
        },
        {
            "schemaVersion",
            1
        },
        {
            "key",
            Key()
        },
        {
            "canonicalKey",
            "100:101:103:IDR"
        },
        {
            "version",
            1L
        },
        {
            "fence",
            0L
        },
        {
            "active",
            true
        },
        {
            "chartVersionId",
            20L
        },
        {
            "closedThrough",
            BsonNull.Value
        }
    };
    internal static BsonDocument Mapping(string key, long id, string code) => new()
    {
        {
            "mappingKey",
            key
        },
        {
            "accountId",
            id
        },
        {
            "accountCode",
            code
        },
        {
            "active",
            true
        }
    };
    internal static BsonDocument Chart() => new()
    {
        {
            "_id",
            20L
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
            "version",
            1L
        },
        {
            "active",
            true
        },
        {
            "mappings",
            new BsonArray
            {
                Mapping("inventory", 501, "INV"),
                Mapping("payable", 502, "AP")
            }
        }
    };
    internal static BsonDocument Period(long id, string month) => new()
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
            "month",
            month
        },
        {
            "version",
            1L
        },
        {
            "dataRevision",
            0L
        },
        {
            "state",
            "open"
        },
        {
            "approvedRevision",
            BsonNull.Value
        },
        {
            "snapshotHash",
            BsonNull.Value
        },
        {
            "lastHistoryId",
            BsonNull.Value
        }
    };
    internal static BsonDocument Line(string key, long id, string code, decimal debit, decimal credit) => new()
    {
        {
            "mappingKey",
            key
        },
        {
            "accountId",
            id
        },
        {
            "accountCode",
            code
        },
        {
            "debit",
            new BsonDecimal128(debit)
        },
        {
            "credit",
            new BsonDecimal128(credit)
        }
    };
    internal static BsonDocument Journal() => new()
    {
        {
            "_id",
            1L
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
            "bookKey",
            Key()
        },
        {
            "source",
            new BsonDocument
            {
                {
                    "namespace",
                    "fixture"
                },
                {
                    "documentId",
                    42L
                },
                {
                    "eventKind",
                    "receipt"
                },
                {
                    "occurrence",
                    1L
                }
            }
        },
        {
            "sourceEvent",
            "fixture:42:receipt:1"
        },
        {
            "sourceRevision",
            1L
        },
        {
            "fingerprint",
            new string ('a', 64)
        },
        {
            "actorId",
            9L
        },
        {
            "actorGrantVersion",
            1L
        },
        {
            "dimensions",
            Dimensions()
        },
        {
            "businessDate",
            "2026-09-28"
        },
        {
            "month",
            "2026-09"
        },
        {
            "currency",
            "IDR"
        },
        {
            "chartId",
            20L
        },
        {
            "chartVersion",
            1L
        },
        {
            "total",
            new BsonDecimal128(100m)
        },
        {
            "lines",
            new BsonArray
            {
                Line("inventory", 501, "INV", 100m, 0m),
                Line("payable", 502, "AP", 0m, 100m)
            }
        },
        {
            "reversalOf",
            BsonNull.Value
        },
        {
            "createdAt",
            new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc)
        },
        {
            "reason",
            "Synthetic test"
        },
        {
            "correlationId",
            "test"
        }
    };
    internal static async Task ConfigureAsync(IMongoDatabase db)
    {
        await db.GetCollection<BsonDocument>("accountingBooks").InsertOneAsync(Book());
        await db.GetCollection<BsonDocument>("accountingChartVersions").InsertOneAsync(Chart());
        await db.GetCollection<BsonDocument>("periods").InsertManyAsync([Period(1, "2026-09"), Period(2, "2026-10"), Period(3, "2026-11")]);
    }
}

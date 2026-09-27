using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class MigrationSchemaTests(MongoFixture fixture)
{
    [Fact]
    public async Task Authorization_documents_reject_weak_ids_missing_fences_and_untyped_scope()
    {
        var name = "ut_auth_schema_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            await new CollectionMigrations(database, new SystemClock()).ApplyAsync();
            var identities = database.GetCollection<BsonDocument>("identities");
            var identity = new BsonDocument
            {
                { "_id", 9_007_199_254_740_993L }, { "schemaVersion", 1 }, { "issuer", "fixture" },
                { "subject", "precise-id" }, { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
            };
            await identities.InsertOneAsync(identity);
            Assert.Equal(9_007_199_254_740_993L,
                (await identities.Find(new BsonDocument("subject", "precise-id")).SingleAsync())["_id"].AsInt64);
            foreach (var change in new Action<BsonDocument>[]
            {
                document => document["_id"] = 2,
                document => document["_id"] = "2",
                document => document["grantVersion"] = 1,
                document => document.Remove("fence"),
                document => document["fence"] = -1L,
                document => document["status"] = "superuser"
            })
            {
                var invalid = identity.DeepClone().AsBsonDocument;
                invalid["_id"] = 2L;
                invalid["subject"] = Guid.NewGuid().ToString("N");
                change(invalid);
                await Assert.ThrowsAsync<MongoWriteException>(() => identities.InsertOneAsync(invalid));
            }

            var memberships = database.GetCollection<BsonDocument>("memberships");
            var membership = new BsonDocument
            {
                { "_id", 5L }, { "schemaVersion", 1 }, { "userId", identity["_id"] },
                { "scope", new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", 10L }, { "branchId", 20L } } },
                { "status", "active" }, { "approvedBy", 3L }, { "effectiveAt", DateTime.UtcNow },
                { "expiresAt", BsonNull.Value }, { "version", 1L }
            };
            await memberships.InsertOneAsync(membership);
            membership["_id"] = 6L;
            membership["scope"]["branchId"] = 20;
            await Assert.ThrowsAsync<MongoWriteException>(() => memberships.InsertOneAsync(membership));
            membership["scope"]["branchId"] = 20L;
            membership["scope"]["allOrganizations"] = true;
            await Assert.ThrowsAsync<MongoWriteException>(() => memberships.InsertOneAsync(membership));
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Fresh_database_has_both_history_steps_named_auth_indexes_and_no_seeded_accounts()
    {
        var name = "ut_auth_fresh_" + Guid.NewGuid().ToString("N");
        var database = fixture.Client.GetDatabase(name);
        try
        {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Equal(2, await database.GetCollection<BsonDocument>("schemaMigrations")
                .CountDocumentsAsync(new BsonDocument("state", "applied")));
            foreach (var collection in new[]
            {
                "identities", "memberships", "authorizationGrants", "authorizationScopes",
                "authorizationProposals", "authorizationHistory"
            })
                Assert.Equal(0, await database.GetCollection<BsonDocument>(collection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var grants = await (await database.GetCollection<BsonDocument>("authorizationGrants")
                .Indexes.ListAsync()).ToListAsync();
            Assert.Contains(grants, index => index["name"] == "ix_userId_status_effectiveAt");
            Assert.Contains(grants, index => index["name"] == "ix_membershipId_status");
        }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
}

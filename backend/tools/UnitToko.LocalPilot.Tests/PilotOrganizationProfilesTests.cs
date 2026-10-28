using UnitToko.Modules.Identity.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.LocalPilot.Tests;

/// <summary>G-034 for a database that already exists: a pilot seeded before the fix holds organisation profiles written straight into
/// the database (no proposal, no approval history, no receipts), and every organisation, address, policy, procurement and accounting
/// first read answers 409 organization_address_integrity_unavailable for them. The next start approves them again through the
/// organisation service, without deleting or rewriting anything else.</summary>
public sealed class PilotOrganizationProfilesTests
{
    private static readonly long[] Nodes = [10, 11, 12, 100, 101, 102, 103];
    /// <summary>Collections the profile upgrade is allowed to add rows to (and the three it moves: scope versions, profile versions and the counters).</summary>
    private static readonly string[] Touched = ["organizationProfiles", "organizationProposals", "authorizationHistory", "idempotencyRecords", "authorizationScopes",
        "accountProfiles", "memberships", "authorizationGrants", "idCounters", "outboxEvents", "auditEvents"];

    /// <summary>Acting through the service moves the acting person's own read-serialization counter; nothing else of an identity may move.</summary>
    private static BsonDocument[] WithoutCounters(string table, BsonDocument[] rows) => table != "identities" ? rows
        : [.. rows.Select(row => row["_id"].AsInt64 is 890001 or 890002 ? new BsonDocument(row.Elements.Where(element => element.Name != "fence")) : row)];

    /// <summary>What differs between two reads of a collection: row counts, then the first differing row's id and fields.</summary>
    private static IEnumerable<string> Differences(string table, BsonDocument[] before, BsonDocument[] after)
    {
        if (before.Length != after.Length) { yield return table + ": " + before.Length + " rows became " + after.Length; yield break; }
        for (var index = 0; index < before.Length; index++)
            if (!before[index].Equals(after[index]))
            {
                var fields = before[index].Names.Union(after[index].Names).Where(field => !before[index].GetValue(field, BsonNull.Value).Equals(after[index].GetValue(field, BsonNull.Value)));
                yield return table + " row " + before[index].GetValue("_id", BsonNull.Value) + " changed: " + string.Join(", ", fields);
            }
    }

    [MongoPilotFact]
    public async Task A_database_seeded_with_unapproved_profiles_gets_them_approved_through_the_service_and_loses_nothing()
    {
        var uri = Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI")!;
        var parsed = new MongoUrl(uri);
        Assert.True(parsed.Servers.Single() is { Host: "127.0.0.1", Port: 27028 } && parsed.DatabaseName is null, "Only the local test replica set on 127.0.0.1:27028.");
        var name = "unittoko_test_localpilot_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(uri);
        var database = client.GetDatabase(name);
        static PilotAccount Account(long id, string user) => new(id, user + " · SINTETIS", user, "secret-" + id, "12" + id);
        var secrets = new PilotSecrets(Guid.NewGuid().ToString("N"), "client-secret", "certificate-password",
            [Account(1001, "pemohon-a"), Account(1002, "pemohon-b"), Account(2001, "petugas"), Account(2002, "peninjau"),
             .. PilotPersonas.SignedIn.Select(persona => Account(persona.AccountId, persona.Name))]);
        PilotBoundary.UseProfile("review");
        try
        {
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            BsonDocument[] Rows(string table) => [.. database.GetCollection<BsonDocument>(table).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToList()];
            var clock = new SystemClock();
            var addresses = new OrganizationAddressService(database, new MongoUnitOfWork(client), new(database), clock,
                new OrganizationMasterDataService(database, new AuthorizationStore(database, new MongoUnitOfWork(client), clock), new(database), clock));
            var pemohon = new ActorContext(3001, PilotBoundary.Issuer, "local-pilot|3001", 1, DateTimeOffset.UtcNow, FactorAssurance.MultiFactor);
            Task<MongoDB.Bson.BsonDocument> Targets() => addresses.TargetsAsync(pemohon, UnitToko.Modules.Organizations.OrganizationAddressPurpose.Delivery);
            Assert.Contains((await Targets())["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);

            // Make it the database the owner's pilots hold today: the profiles as the old seed wrote them, with no proposal, history or
            // receipt, scope versions back at 1, and none of the two bootstrap people that now name the organisation.
            var marker = database.GetCollection<BsonDocument>("localPilotMetadata").Find(FilterDefinition<BsonDocument>.Empty).Single();
            var seededAt = marker["createdAt"].ToUniversalTime();
            var current = Rows("organizationProfiles");
            var legacy = current.Select(row => new BsonDocument { { "_id", row["_id"] }, { "schemaVersion", 1 }, { "kind", row["kind"] }, { "parentId", row["parentId"] },
                { "code", row["code"] }, { "normalizedCode", row["normalizedCode"] }, { "name", row["name"] }, { "version", 1L }, { "createdBy", 2001L }, { "updatedBy", 2002L },
                { "createdAt", seededAt }, { "updatedAt", seededAt }, { "approvedProposalId", row["_id"].AsInt64 + 10000 } }).ToArray();
            await database.GetCollection<BsonDocument>("organizationProfiles").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await database.GetCollection<BsonDocument>("organizationProfiles").InsertManyAsync(legacy);
            await database.GetCollection<BsonDocument>("organizationProposals").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await database.GetCollection<BsonDocument>("authorizationHistory").DeleteManyAsync(new BsonDocument("operation", new BsonRegularExpression("^organization\\.")));
            await database.GetCollection<BsonDocument>("idempotencyRecords").DeleteManyAsync(new BsonDocument("operation", new BsonRegularExpression("^organization\\.")));
            await database.GetCollection<BsonDocument>("authorizationScopes").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument { { "version", 1L }, { "fence", 0L } }));
            var people = new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 890001L, 890002L }));
            foreach (var table in new[] { "accountProfiles", "memberships", "authorizationGrants" }) await database.GetCollection<BsonDocument>(table).DeleteManyAsync(people);
            await Assert.ThrowsAsync<PersistenceConflictException>(Targets);

            string[] tables = [.. (await (await database.ListCollectionNamesAsync()).ToListAsync()).Order(StringComparer.Ordinal)];
            var before = tables.ToDictionary(table => table, table => Rows(table));

            // The next start approves every profile again through the service, and changes nothing outside the approval records.
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            var changed = new List<string>();
            foreach (var table in tables)
            {
                var after = Rows(table);
                if (Touched.Contains(table)) { if (after.Length < before[table].Length) changed.Add(table + " lost rows"); }
                else changed.AddRange(Differences(table, WithoutCounters(table, before[table]), WithoutCounters(table, after)));
            }
            Assert.Empty(changed);
            Assert.Contains((await Targets())["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);
            var profiles = Rows("organizationProfiles");
            Assert.Equal(Nodes.Order(), profiles.Select(row => row["_id"].AsInt64).Order());
            for (var index = 0; index < profiles.Length; index++)
            {
                // Same name and code as before, one version later; the proposer and approver are the two different bootstrap people.
                Assert.Equal((legacy[index]["code"], legacy[index]["name"], 2L), (profiles[index]["code"], profiles[index]["name"], profiles[index]["version"].AsInt64));
                var node = profiles[index]["_id"].AsInt64;
                var proposal = Assert.Single(Rows("organizationProposals"), row => row["scopeId"] == node);
                Assert.Equal(("approved", 890001L, 890002L, 1L), (proposal["status"].AsString, proposal["requestedBy"].AsInt64, proposal["approvedBy"].AsInt64, proposal["expectedProfileVersion"].AsInt64));
                Assert.Single(Rows("authorizationHistory"), row => row["operation"] == "organization.proposed" && row["after"]["_id"] == proposal["_id"]);
                Assert.Single(Rows("authorizationHistory"), row => row["operation"] == "organization.approved" && row["after"]["profile"]["_id"] == node && row["after"]["profile"]["version"] == 2L);
            }
            Assert.All(Rows("authorizationScopes"), row => Assert.Equal(2L, row["version"].AsInt64));
            // Only the two bootstrap people were added to the people tables; nobody else's account, membership or grant moved.
            foreach (var table in new[] { "accountProfiles", "memberships", "authorizationGrants" })
                Assert.Equal(before[table].Select(row => row["_id"].AsInt64).Concat([890001L, 890002L]).Order(), Rows(table).Select(row => row["_id"].AsInt64).Order());

            // Done once: a further start approves nothing.
            var settled = tables.ToDictionary(table => table, table => Rows(table));
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Empty(tables.Where(table => table != "idCounters").SelectMany(table => Differences(table, settled[table], Rows(table))));
        }
        finally
        {
            PilotBoundary.UseProfile("release1");
            if (name.StartsWith("unittoko_test_localpilot_", StringComparison.Ordinal)) await client.DropDatabaseAsync(name);
        }
    }
}

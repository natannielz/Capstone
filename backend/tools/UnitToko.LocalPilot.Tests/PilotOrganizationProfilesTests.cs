using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
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
    /// <summary>Collections the profile upgrade is allowed to add rows to (and the ones it moves: scope versions, profile versions and the counters).</summary>
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

    /// <summary>Every row that existed before is still there, equal except for the named fields (which the upgrade may move).</summary>
    private static IEnumerable<string> RowsKept(string table, BsonDocument[] before, BsonDocument[] after, params string[] movable)
    {
        var current = after.ToDictionary(row => row["_id"]);
        foreach (var row in before)
        {
            if (!current.TryGetValue(row["_id"], out var now)) { yield return table + " row " + row["_id"] + " is gone"; continue; }
            BsonDocument Strip(BsonDocument value) => new(value.Elements.Where(element => !movable.Contains(element.Name)));
            if (!Strip(row).Equals(Strip(now))) yield return table + " row " + row["_id"] + " was rewritten";
        }
    }

    /// <summary>A disposable pilot database on the local test replica set, bootstrapped as the review profile, that can be turned into the
    /// shape the owner's pilots hold today.</summary>
    private sealed class Pilot : IAsyncDisposable
    {
        public MongoClient Client { get; }
        public IMongoDatabase Database { get; }
        public PilotSecrets Secrets { get; }
        public string Name { get; } = "unittoko_test_localpilot_" + Guid.NewGuid().ToString("N");
        public DateTime SeededAt { get; private set; }
        public BsonDocument[] Legacy { get; private set; } = [];
        public string StateDirectory { get; } = Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N"));
        public string MarkerPath => Path.Combine(StateDirectory, "profile-upgrade-failed.txt");

        public Pilot()
        {
            var uri = Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI")!;
            var parsed = new MongoUrl(uri);
            Assert.True(parsed.Servers.Single() is { Host: "127.0.0.1", Port: 27028 } && parsed.DatabaseName is null, "Only the local test replica set on 127.0.0.1:27028.");
            Client = new MongoClient(uri); Database = Client.GetDatabase(Name);
            static PilotAccount Account(long id, string user) => new(id, user + " · SINTETIS", user, "secret-" + id, "12" + id);
            Secrets = new PilotSecrets(Guid.NewGuid().ToString("N"), "client-secret", "certificate-password",
                [Account(1001, "pemohon-a"), Account(1002, "pemohon-b"), Account(2001, "petugas"), Account(2002, "peninjau"),
                 .. PilotPersonas.SignedIn.Select(persona => Account(persona.AccountId, persona.Name))]);
            Directory.CreateDirectory(StateDirectory);
            PilotBoundary.UseProfile("review");
        }

        public BsonDocument[] Rows(string table) => [.. Database.GetCollection<BsonDocument>(table).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToList()];
        public async Task<string[]> TablesAsync() => [.. (await (await Database.ListCollectionNamesAsync()).ToListAsync()).Order(StringComparer.Ordinal)];
        public async Task<Dictionary<string, BsonDocument[]>> SnapshotAsync() => (await TablesAsync()).ToDictionary(table => table, table => Rows(table));
        public Task StartAsync() => PilotBootstrap.InitializeDatabaseAsync(Client, Database, Secrets, CancellationToken.None, StateDirectory);

        /// <summary>The first read an address picker makes, for a requester of branch 11: 409 until the profiles are approved.</summary>
        public Task<BsonDocument> TargetsAsync()
        {
            var clock = new SystemClock();
            var addresses = new OrganizationAddressService(Database, new MongoUnitOfWork(Client), new(Database), clock,
                new OrganizationMasterDataService(Database, new AuthorizationStore(Database, new MongoUnitOfWork(Client), clock), new(Database), clock));
            var pemohon = new ActorContext(3001, PilotBoundary.Issuer, "local-pilot|3001", 1, DateTimeOffset.UtcNow, FactorAssurance.MultiFactor);
            return addresses.TargetsAsync(pemohon, UnitToko.Modules.Organizations.OrganizationAddressPurpose.Delivery);
        }

        /// <summary>The profiles as the old seed wrote them, with no proposal, history or receipt, scope versions back at 1, and none of the
        /// two bootstrap people that now name the organisation.</summary>
        public async Task MakeLegacyAsync()
        {
            var marker = Database.GetCollection<BsonDocument>("localPilotMetadata").Find(FilterDefinition<BsonDocument>.Empty).Single();
            SeededAt = marker["createdAt"].ToUniversalTime();
            Legacy = [.. Rows("organizationProfiles").Select(row => new BsonDocument { { "_id", row["_id"] }, { "schemaVersion", 1 }, { "kind", row["kind"] }, { "parentId", row["parentId"] },
                { "code", row["code"] }, { "normalizedCode", row["normalizedCode"] }, { "name", row["name"] }, { "version", 1L }, { "createdBy", 2001L }, { "updatedBy", 2002L },
                { "createdAt", SeededAt }, { "updatedAt", SeededAt }, { "approvedProposalId", row["_id"].AsInt64 + 10000 } })];
            await Database.GetCollection<BsonDocument>("organizationProfiles").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await Database.GetCollection<BsonDocument>("organizationProfiles").InsertManyAsync(Legacy);
            await Database.GetCollection<BsonDocument>("organizationProposals").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await Database.GetCollection<BsonDocument>("authorizationHistory").DeleteManyAsync(new BsonDocument("operation", new BsonRegularExpression("^organization\\.")));
            await Database.GetCollection<BsonDocument>("idempotencyRecords").DeleteManyAsync(new BsonDocument("operation", new BsonRegularExpression("^organization\\.")));
            await Database.GetCollection<BsonDocument>("authorizationScopes").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument { { "version", 1L }, { "fence", 0L } }));
            var people = new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 890001L, 890002L }));
            foreach (var table in new[] { "accountProfiles", "memberships", "authorizationGrants" }) await Database.GetCollection<BsonDocument>(table).DeleteManyAsync(people);
        }

        public async ValueTask DisposeAsync()
        {
            PilotBoundary.UseProfile("release1");
            if (Name.StartsWith("unittoko_test_localpilot_", StringComparison.Ordinal)) await Client.DropDatabaseAsync(Name);
            if (StateDirectory.Contains("UnitToko-LocalPilot-Tests", StringComparison.Ordinal) && Directory.Exists(StateDirectory)) Directory.Delete(StateDirectory, true);
        }
    }

    [MongoPilotFact]
    public async Task A_database_seeded_with_unapproved_profiles_gets_them_approved_through_the_service_and_loses_nothing()
    {
        await using var pilot = new Pilot();
        await pilot.StartAsync();
        Assert.Contains((await pilot.TargetsAsync())["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);
        await pilot.MakeLegacyAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(pilot.TargetsAsync);

        string[] tables = await pilot.TablesAsync();
        var before = await pilot.SnapshotAsync();

        // The next start approves every profile again through the service, and changes nothing outside the approval records.
        await pilot.StartAsync();
        var changed = new List<string>();
        foreach (var table in tables)
        {
            var after = pilot.Rows(table);
            changed.AddRange(table switch
            {
                // Rows the upgrade may add to; every row that was there stays equal. Scopes move only their version and fence, identities only
                // the two bootstrap people's fence, the counters only grow, and the profiles are replaced by a revised row (checked below).
                "authorizationScopes" => RowsKept(table, before[table], after, "version", "fence"),
                "organizationProfiles" or "idCounters" => [],
                _ when Touched.Contains(table) => RowsKept(table, before[table], after),
                _ => Differences(table, WithoutCounters(table, before[table]), WithoutCounters(table, after)),
            });
        }
        Assert.Empty(changed);
        Assert.Contains((await pilot.TargetsAsync())["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);
        var profiles = pilot.Rows("organizationProfiles");
        Assert.Equal(Nodes.Order(), profiles.Select(row => row["_id"].AsInt64).Order());
        for (var index = 0; index < profiles.Length; index++)
        {
            // Same name and code as before, one version later, still created by the people who created it; the proposer and approver are the
            // two different bootstrap people.
            Assert.Equal((pilot.Legacy[index]["code"], pilot.Legacy[index]["name"], 2L), (profiles[index]["code"], profiles[index]["name"], profiles[index]["version"].AsInt64));
            Assert.Equal((pilot.Legacy[index]["createdBy"], pilot.Legacy[index]["createdAt"]), (profiles[index]["createdBy"], profiles[index]["createdAt"]));
            var node = profiles[index]["_id"].AsInt64;
            var proposal = Assert.Single(pilot.Rows("organizationProposals"), row => row["scopeId"] == node);
            Assert.Equal(("approved", 890001L, 890002L, 1L), (proposal["status"].AsString, proposal["requestedBy"].AsInt64, proposal["approvedBy"].AsInt64, proposal["expectedProfileVersion"].AsInt64));
            Assert.Single(pilot.Rows("authorizationHistory"), row => row["operation"] == "organization.proposed" && row["after"]["_id"] == proposal["_id"]);
            Assert.Single(pilot.Rows("authorizationHistory"), row => row["operation"] == "organization.approved" && row["after"]["profile"]["_id"] == node && row["after"]["profile"]["version"] == 2L);
        }
        Assert.All(pilot.Rows("authorizationScopes"), row => Assert.Equal(2L, row["version"].AsInt64));
        // Only the two bootstrap people were added to the people tables; nobody else's account, membership or grant moved.
        foreach (var table in new[] { "accountProfiles", "memberships", "authorizationGrants" })
            Assert.Equal(before[table].Select(row => row["_id"].AsInt64).Concat([890001L, 890002L]).Order(), pilot.Rows(table).Select(row => row["_id"].AsInt64).Order());

        // Done once: a further start approves nothing.
        var settled = await pilot.SnapshotAsync();
        await pilot.StartAsync();
        Assert.Empty(tables.Where(table => table != "idCounters").SelectMany(table => Differences(table, settled[table], pilot.Rows(table))));
    }

    [MongoPilotFact]
    public async Task A_failed_upgrade_stops_the_review_stack_and_only_warns_on_the_full_pilot_and_the_next_start_resumes_and_release1_is_left_alone()
    {
        await using var pilot = new Pilot();
        await pilot.StartAsync();
        await pilot.MakeLegacyAsync();
        var untouched = await pilot.SnapshotAsync();

        // The persistent Release 1 pilot keeps its legacy profiles: the upgrade does nothing there, not even to the state folder.
        PilotBoundary.UseProfile("release1");
        try { await PilotBootstrap.UpgradeOrganizationProfilesAsync(pilot.Database, pilot.Client, pilot.SeededAt, failClosed: false, pilot.StateDirectory, CancellationToken.None); }
        finally { PilotBoundary.UseProfile("review"); }
        var afterRelease1 = await pilot.SnapshotAsync();
        Assert.Empty(untouched.SelectMany(table => Differences(table.Key, table.Value, afterRelease1[table.Key])));
        Assert.False(File.Exists(pilot.MarkerPath));
        Assert.Null(PilotBootstrap.UpgradeWarning);

        // Make the approver unable to approve (its identity's grant version no longer matches the one it acts with): the proposal is made, the approval is refused.
        await PilotBootstrap.EnsureProfileActorsAsync(pilot.Database, pilot.SeededAt, CancellationToken.None);
        var identities = pilot.Database.GetCollection<BsonDocument>("identities");
        Assert.Equal(1, (await identities.UpdateOneAsync(new BsonDocument("_id", 890002L), new BsonDocument("$set", new BsonDocument("grantVersion", 2L)))).ModifiedCount);

        // Review: the start fails before it could print "ready", and writes no marker (the next start simply retries).
        await Assert.ThrowsAnyAsync<Exception>(pilot.StartAsync);
        Assert.False(File.Exists(pilot.MarkerPath));
        Assert.Single(pilot.Rows("organizationProposals"), row => row["status"] == "pending");

        // Full: the same failure does not stop the start. It leaves the marker (time and exception type only) and the warning for the launcher.
        await PilotBootstrap.UpgradeOrganizationProfilesAsync(pilot.Database, pilot.Client, pilot.SeededAt, failClosed: false, pilot.StateDirectory, CancellationToken.None);
        var marker = File.ReadAllLines(pilot.MarkerPath);
        Assert.Equal(2, marker.Length);
        Assert.True(DateTime.TryParse(marker[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.Matches("^[A-Za-z]+Exception$", marker[1]);
        Assert.Contains("409", PilotBootstrap.UpgradeWarning);
        Assert.Contains("Mulai ulang", PilotBootstrap.UpgradeWarning);
        Assert.DoesNotContain("mongodb://", PilotBootstrap.UpgradeWarning + string.Join("", marker));
        // The second try resumed the pending proposal instead of making another one.
        Assert.Single(pilot.Rows("organizationProposals"));

        // The approver is able again: the next start approves the pending proposal once (no second proposal) and the rest, and removes the marker.
        Assert.Equal(1, (await identities.UpdateOneAsync(new BsonDocument("_id", 890002L), new BsonDocument("$set", new BsonDocument("grantVersion", 1L)))).ModifiedCount);
        await pilot.StartAsync();
        Assert.False(File.Exists(pilot.MarkerPath));
        Assert.Null(PilotBootstrap.UpgradeWarning);
        var proposals = pilot.Rows("organizationProposals");
        Assert.Equal(Nodes.Order(), proposals.Select(row => row["scopeId"].AsInt64).Order());
        Assert.All(proposals, row => Assert.Equal(("approved", 890001L, 890002L), (row["status"].AsString, row["requestedBy"].AsInt64, row["approvedBy"].AsInt64)));
        Assert.Equal(7, pilot.Rows("authorizationHistory").Count(row => row["operation"] == "organization.approved"));
        Assert.Contains((await pilot.TargetsAsync())["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);
    }
}

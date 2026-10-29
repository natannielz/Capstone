using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

// The integration test below switches the process-wide pilot profile; nothing else may run beside it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UnitToko.LocalPilot.Tests;

public sealed class PilotSamplesTests
{
    [Fact]
    public void Twelve_demo_products_three_per_group_are_chosen_and_only_missing_ones_are_proposed()
    {
        var chosen = PilotSamples.ChosenSkus();
        Assert.Equal(PilotSamples.Count, chosen.Length);
        Assert.Equal(chosen.Length, chosen.Distinct().Count());
        var products = PilotCatalog.DemoProducts().ToDictionary(product => product.Sku);
        Assert.All(products.Values.GroupBy(product => product.Group), group => Assert.Equal(3, chosen.Count(sku => products[sku].Group == group.Key)));
        Assert.All(chosen, sku => Assert.False(string.IsNullOrWhiteSpace(products[sku].Image)));
        Assert.Equal(new long[] { 3, 5 }, PilotSamples.Missing([1, 2, 3, 4, 5], new HashSet<long> { 1, 4 }, new HashSet<long> { 2 }));
        Assert.Empty(PilotSamples.Missing([1, 2], new HashSet<long> { 1, 2 }, new HashSet<long>()));
    }

    [Fact]
    public void Only_an_owner_outside_petugas_scope_is_skipped_while_a_missing_propose_grant_fails_the_start()
    {
        // scope_denied: the service's refusal for a catalog owner outside petugas's scope; that owner's staff decide.
        Assert.True(PilotSamples.IsOutsideOwnerScope(new AuthorizationDeniedException("scope_denied")));
        // permission_denied (and anything else): petugas holds no usable catalog.public.propose at all; the start must fail.
        Assert.False(PilotSamples.IsOutsideOwnerScope(new AuthorizationDeniedException("permission_denied")));
        Assert.False(PilotSamples.IsOutsideOwnerScope(new AuthorizationDeniedException("step_up_required")));
    }

    [Fact]
    public async Task A_sample_conflict_is_logged_and_skipped_and_anything_else_still_fails()
    {
        var console = new StringWriter(); var previous = Console.Out; Console.SetOut(console);
        try
        {
            foreach (var code in new[] { "public_sample_limit", "catalog_public_sample_pending", "catalog_public_sample_unchanged", "catalog_public_sample_changed",
                "catalog_public_sample_decided" })
                Assert.False(await PilotSamples.SkipOnConflictAsync(() => throw new PersistenceConflictException(code, "synthetic"), "uji"));
            Assert.True(await PilotSamples.SkipOnConflictAsync(() => Task.CompletedTask, "uji"));
            await Assert.ThrowsAsync<PersistenceConflictException>(() => PilotSamples.SkipOnConflictAsync(() => throw new PersistenceConflictException("other_conflict", "x"), "uji"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => PilotSamples.SkipOnConflictAsync(() => throw new InvalidOperationException("x"), "uji"));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains("catalog_public_sample_changed", console.ToString());
        Assert.Contains("Sampel tamu", console.ToString());
    }

    [MongoPilotFact]
    public async Task Review_bootstrap_seeds_twelve_guest_samples_through_the_maker_checker_service_once()
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
            var families = PilotSamples.ChosenSkus().Select(sku => database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("sku", sku)).Single()["familyId"].AsInt64).ToArray();

            // G-034: every organisation profile is named through the application (technical.configure proposes, a different person with
            // organization.approve approves), so it carries the approval history the first reads require.
            foreach (var node in new long[] { 10, 11, 12, 100, 101, 102, 103 })
            {
                Assert.Single(Rows("organizationProfiles"), row => row["_id"] == node);
                var named = Assert.Single(Rows("organizationProposals"), row => row["scopeId"] == node);
                Assert.Equal(("approved", 890001L, 890002L), (named["status"].AsString, named["requestedBy"].AsInt64, named["approvedBy"].AsInt64));
                Assert.Single(Rows("authorizationHistory"), row => row["operation"] == "organization.approved" && row["after"]["profile"]["_id"] == node);
            }
            var store = new AuthorizationStore(database, new MongoUnitOfWork(client), new SystemClock());
            var addresses = new OrganizationAddressService(database, new MongoUnitOfWork(client), new(database), new SystemClock(),
                new OrganizationMasterDataService(database, store, new(database), new SystemClock()));
            var pemohon = new ActorContext(3001, PilotBoundary.Issuer, "local-pilot|3001", 1, DateTimeOffset.UtcNow, FactorAssurance.MultiFactor);
            var targets = await addresses.TargetsAsync(pemohon, UnitToko.Modules.Organizations.OrganizationAddressPurpose.Delivery);
            Assert.Contains(targets["items"].AsBsonArray, item => item["target"]["id"].AsInt64 == 11);

            // Twelve samples, exactly the chosen products, through one proposal by petugas approved by peninjau.
            var samples = Rows("catalogPublicSamples");
            Assert.Equal(families.Order(), samples.Select(row => row["_id"].AsInt64).Order());
            var proposal = Assert.Single(Rows("catalogPublicSampleProposals"));
            Assert.Equal("approved", proposal["state"].AsString);
            Assert.Equal(2001, proposal["proposedBy"].AsInt64);
            Assert.Equal(2002, proposal["decision"]["decidedBy"].AsInt64);
            Assert.Equal(families.Order(), proposal["adds"].AsBsonArray.Select(value => value.AsInt64).Order());
            Assert.Equal(2, Rows("auditEvents").Count(row => row["kind"] == "catalog.public.sample.command"));

            // A restart leaves the approved set alone: no proposal, no audit, no row touched.
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(samples, Rows("catalogPublicSamples"));
            Assert.Single(Rows("catalogPublicSampleProposals"));
            Assert.Equal(2, Rows("auditEvents").Count(row => row["kind"] == "catalog.public.sample.command"));

            // A chosen product removed through the service comes back on the next start through a new proposal; the rest stay as approved.
            var clock = new SystemClock();
            var service = new CatalogPublicSampleService(database, new MongoUnitOfWork(client), new(database), clock);
            ActorContext Actor(long id) => new(id, PilotBoundary.Issuer, "local-pilot|" + id, 1, clock.UtcNow, FactorAssurance.MultiFactor);
            var removed = await service.ProposeAsync(Actor(2001), [], [families[0]], new("test.remove", "Uji: hapus satu contoh", "local-pilot-tests"));
            await service.DecideAsync(Actor(2002), removed["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve, new("test.remove.ok", "Uji: disetujui", "local-pilot-tests"));
            Assert.Equal(11, Rows("catalogPublicSamples").Length);
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            var after = Rows("catalogPublicSamples");
            Assert.Equal(families.Order(), after.Select(row => row["_id"].AsInt64).Order());
            Assert.Equal(samples.Where(row => row["_id"].AsInt64 != families[0]), after.Where(row => row["_id"].AsInt64 != families[0]));
            var readded = Rows("catalogPublicSampleProposals").Last();
            Assert.Equal(new[] { families[0] }, readded["adds"].AsBsonArray.Select(value => value.AsInt64));
            Assert.Equal((2001L, 2002L, "approved"), (readded["proposedBy"].AsInt64, readded["decision"]["decidedBy"].AsInt64, readded["state"].AsString));
            async Task RemoveAsync(long family, string key)
            {
                var proposal = await service.ProposeAsync(Actor(2001), [], [family], new("test.remove." + key, "Uji: hapus satu contoh", "local-pilot-tests"));
                await service.DecideAsync(Actor(2002), proposal["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve, new("test.remove.ok." + key, "Uji: disetujui", "local-pilot-tests"));
            }
            int Proposals() => Rows("catalogPublicSampleProposals").Length;

            // The same product removed a second time comes back again: every seeding attempt is its own command, never a replay.
            await RemoveAsync(families[0], "second");
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(families.Order(), Rows("catalogPublicSamples").Select(row => row["_id"].AsInt64).Order());
            Assert.Equal(new[] { families[0] }, Rows("catalogPublicSampleProposals").Last()["adds"].AsBsonArray.Select(value => value.AsInt64));

            // A person's own pending proposal (made as petugas in the review screens) is theirs to finish: seeding neither approves it
            // nor proposes the same product again.
            await RemoveAsync(families[1], "person");
            var person = await service.ProposeAsync(Actor(2001), [families[1]], [], new("review-ui.person-proposal", "Diajukan dari layar ulasan", "review-ui"));
            var before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal("pending", Rows("catalogPublicSampleProposals").Single(row => row["_id"] == person["proposalId"])["state"].AsString);
            Assert.Equal(before, Proposals());
            Assert.Equal(11, Rows("catalogPublicSamples").Length);
            await service.DecideAsync(Actor(2002), person["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve, new("review-ui.person-approve", "Diperiksa", "review-ui"));

            // The pilot's own proposal left pending by an interrupted start is approved by peninjau, without a second proposal.
            await RemoveAsync(families[3], "interrupted");
            var interrupted = await service.ProposeAsync(Actor(2001), [families[3]], [], new("local-pilot.samples-v1.propose.interrupted-test", "SYNTHETIC: terputus", "local-pilot-samples"));
            before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            var resumed = Rows("catalogPublicSampleProposals").Single(row => row["_id"] == interrupted["proposalId"]);
            Assert.Equal(("approved", 2002L), (resumed["state"].AsString, resumed["decision"]["decidedBy"].AsInt64));
            Assert.Equal(before, Proposals());
            Assert.Equal(12, Rows("catalogPublicSamples").Length);

            // Seeding never pushes past the sample limit, and never fails the start for it: it says so and skips.
            await RemoveAsync(families[2], "cap");
            before = Proposals();
            var console = new StringWriter(); var previous = Console.Out; Console.SetOut(console);
            try { await PilotSamples.SeedAsync(database, client, secrets, CancellationToken.None, limit: 11); }
            finally { Console.SetOut(previous); }
            Assert.Contains("batas 11", console.ToString());
            Assert.Equal(before, Proposals());
            Assert.Equal(11, Rows("catalogPublicSamples").Length);
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(families.Order(), Rows("catalogPublicSamples").Select(row => row["_id"].AsInt64).Order());

            // A staff-chosen extra product inside the cap is left alone.
            var extra = database.GetCollection<BsonDocument>("skus").Find(new BsonDocument { { "sku", "DEMO-004" }, { "active", true } }).Single()["familyId"].AsInt64;
            var staff = await service.ProposeAsync(Actor(2001), [extra], [], new("review-ui.staff-extra", "Pilihan staf", "review-ui"));
            await service.DecideAsync(Actor(2002), staff["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve, new("review-ui.staff-extra-ok", "Diperiksa", "review-ui"));
            before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(13, Rows("catalogPublicSamples").Length);
            Assert.Equal(before, Proposals());

            // Over-cap fixture: what the version 122 conversion of a public assortment wrote (every published product a sample). Only this
            // fixture writes the collection directly; the pilot narrows it back through the service.
            var published = database.GetCollection<BsonDocument>("skus").Distinct<long>("familyId",
                new BsonDocument { { "active", true }, { "catalogListings.assortmentId", 501L } }).ToList();
            var present = Rows("catalogPublicSamples").Select(row => row["_id"].AsInt64).ToHashSet();
            var converted = database.GetCollection<BsonDocument>("productFamilies").Find(new BsonDocument("active", true)).ToList()
                .Where(row => published.Contains(row["_id"].AsInt64) && !present.Contains(row["_id"].AsInt64))
                .Select(row => new BsonDocument { { "_id", row["_id"] }, { "schemaVersion", 1 }, { "ownerScope", row["authoringOwner"] }, { "revision", 1L },
                    { "addedAt", DateTime.UtcNow.AddMinutes(-1) }, { "approvedProposalId", BsonNull.Value },
                    { "migratedFrom", new BsonDocument { { "assortmentId", 501L }, { "showcaseDecisionId", 1L } } } }).ToList();
            await database.GetCollection<BsonDocument>("catalogPublicSamples").InsertManyAsync(converted);
            var overCap = Rows("catalogPublicSamples").Length;
            Assert.True(overCap > 200, "fixture must exceed the cap, has " + overCap);
            before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            // Narrowed to exactly the chosen twelve, through removal proposals by petugas (at most 200 products each) approved by peninjau.
            Assert.Equal(families.Order(), Rows("catalogPublicSamples").Select(row => row["_id"].AsInt64).Order());
            var narrowing = Rows("catalogPublicSampleProposals").Skip(before).ToArray();
            Assert.Equal((overCap - 12 + 199) / 200, narrowing.Length);
            Assert.All(narrowing, row =>
            {
                Assert.Equal(("approved", 2001L, 2002L), (row["state"].AsString, row["proposedBy"].AsInt64, row["decision"]["decidedBy"].AsInt64));
                Assert.Empty(row["adds"].AsBsonArray);
                Assert.InRange(row["removes"].AsBsonArray.Count, 1, 200);
                Assert.DoesNotContain(row["removes"].AsBsonArray, id => families.Contains(id.AsInt64));
                Assert.StartsWith(PilotSamples.ProposeKeyPrefix, row["requestKey"].AsString);
            });
            Assert.Equal(overCap - 12, narrowing.Sum(row => row["removes"].AsBsonArray.Count));
            // Back inside the cap, a restart changes nothing.
            before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(before, Proposals());

            // An interrupted narrowing converges: over the cap again, the pilot's first removal batch was approved and its second left
            // pending when the start stopped. The set is now under the cap, yet the next start finishes the pilot's own narrowing (approving
            // the pending batch) instead of keeping the leftovers as if staff had chosen them.
            await database.GetCollection<BsonDocument>("catalogPublicSamples").InsertManyAsync(converted.Select(row => row.DeepClone().AsBsonDocument));
            Assert.True(Rows("catalogPublicSamples").Length > 200);
            var leftovers = converted.Select(row => row["_id"].AsInt64).Order().ToArray();
            var first = await service.ProposeAsync(Actor(2001), [], [.. leftovers.Take(200)], new(PilotSamples.ProposeKeyPrefix + "interrupted-batch-1", "SYNTHETIC: dipersempit", "local-pilot-samples"));
            await service.DecideAsync(Actor(2002), first["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve, new("local-pilot.samples-v1.approve.interrupted-batch-1", "SYNTHETIC: diperiksa", "local-pilot-samples"));
            var second = await service.ProposeAsync(Actor(2001), [], [.. leftovers.Skip(200)], new(PilotSamples.ProposeKeyPrefix + "interrupted-batch-2", "SYNTHETIC: dipersempit", "local-pilot-samples"));
            Assert.InRange(Rows("catalogPublicSamples").Length, 13, 200);
            before = Proposals();
            await PilotBootstrap.InitializeDatabaseAsync(client, database, secrets, CancellationToken.None);
            Assert.Equal(families.Order(), Rows("catalogPublicSamples").Select(row => row["_id"].AsInt64).Order());
            var finished = Rows("catalogPublicSampleProposals").Single(row => row["_id"] == second["proposalId"]);
            Assert.Equal(("approved", 2002L), (finished["state"].AsString, finished["decision"]["decidedBy"].AsInt64));
            Assert.Equal(before, Proposals());
            Assert.DoesNotContain(Rows("catalogPublicSampleProposals"), row => row["removes"].AsBsonArray.Any(id => families.Contains(id.AsInt64)) &&
                row["requestKey"].AsString.StartsWith(PilotSamples.ProposeKeyPrefix, StringComparison.Ordinal));

            // Guests see exactly these twelve, and no price anywhere in the answer.
            await using var api = new PublicApi(uri, name);
            using var guest = api.CreateClient();
            using var response = await guest.GetAsync("/api/v1/public/catalog?limit=48");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.Equal("12", json.RootElement.GetProperty("total").GetString());
            var names = PilotSamples.ChosenSkus().Select(sku => PilotCatalog.DemoProducts().Single(product => product.Sku == sku).Name).Order(StringComparer.Ordinal);
            Assert.Equal(names, json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
            static IEnumerable<string> Properties(JsonElement element) => element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Properties(property.Value).Prepend(property.Name)),
                JsonValueKind.Array => element.EnumerateArray().SelectMany(Properties),
                _ => [],
            };
            Assert.DoesNotContain(Properties(json.RootElement), property => property.Contains("price", StringComparison.OrdinalIgnoreCase) ||
                property.Contains("harga", StringComparison.OrdinalIgnoreCase) || property.Contains("amount", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PilotBoundary.UseProfile("release1");
            if (name.StartsWith("unittoko_test_localpilot_", StringComparison.Ordinal)) await client.DropDatabaseAsync(name);
        }
    }

    /// <summary>The real API host on the test database, anonymous only (sign-in is not configured).</summary>
    private sealed class PublicApi(string uri, string database) : WebApplicationFactory<global::Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = uri, ["Mongo:Database"] = database, ["Release:Profile"] = "full", ["Logging:LogLevel:Default"] = "Warning",
            }));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production").UseContentRoot(Api());
        private static string Api()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "UnitToko.Api", "UnitToko.Api.csproj"))) return Path.Combine(directory.FullName, "backend", "src", "UnitToko.Api");
            throw new InvalidOperationException("Run from inside the repository.");
        }
    }
}

public sealed class MongoPilotFactAttribute : FactAttribute
{
    public MongoPilotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI")))
            Skip = "Set UNITTOKO_TEST_MONGO_URI to the local test replica set (127.0.0.1:27028).";
    }
}

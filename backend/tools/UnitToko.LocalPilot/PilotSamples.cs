using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

/// <summary>
/// The guest sample: 12 products of the first demo (the first three of each group, in file order: each has a published package and
/// a demo photo). They become samples only through <see cref="CatalogPublicSampleService"/>: <c>petugas</c> (2001,
/// catalog.public.propose) proposes and <c>peninjau</c> (2002, catalog.public.approve, fresh factor) approves. Nothing here writes
/// the sample collections. On every start a sample that is already approved is left alone; a chosen product that is missing is
/// added through a new proposal; a proposal left pending by an interrupted start is approved.
/// </summary>
public static class PilotSamples
{
    public const int Count = 12;
    private const long Maker = 2001, Checker = 2002;
    private const int Limit = 200;

    /// <summary>The demo SKU codes chosen as guest samples.</summary>
    public static string[] ChosenSkus() =>
        [.. PilotCatalog.DemoProducts().GroupBy(product => product.Group, StringComparer.Ordinal).SelectMany(group => group.Take(3)).Select(product => product.Sku)];

    public static async Task SeedAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, CancellationToken ct)
    {
        var clock = new SystemClock();
        var service = new CatalogPublicSampleService(database, new MongoUnitOfWork(client), new(database), clock);
        // Fresh factor: each actor is built right before its command, as a signed-in person who just completed MFA.
        ActorContext Actor(long id) => new(id, PilotBoundary.Issuer, secrets.Accounts.Single(account => account.Id == id).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        static AuthorizationCommandMetadata Meta(string key, string reason) => new("local-pilot.samples-v1." + key, reason, "local-pilot-samples");

        var codes = ChosenSkus();
        var chosen = new List<long>();
        foreach (var code in codes)
        {
            var sku = await database.GetCollection<BsonDocument>("skus").Find(new BsonDocument { { "sku", code }, { "active", true }, { "catalogListings.assortmentId", 501L } })
                .Project(new BsonDocument("familyId", 1)).FirstOrDefaultAsync(ct);
            if (sku is not null && !chosen.Contains(sku["familyId"].AsInt64)) chosen.Add(sku["familyId"].AsInt64);
        }
        if (chosen.Count != Count) { Console.WriteLine("Sampel tamu: hanya " + chosen.Count + " dari " + Count + " produk contoh tersedia; dilewati."); return; }

        // An interrupted start may have left our own proposal waiting: approve it rather than proposing again.
        var proposals = database.GetCollection<BsonDocument>("catalogPublicSampleProposals");
        var pending = await proposals.Find(new BsonDocument { { "state", "pending" } }).ToListAsync(ct);
        foreach (var proposal in pending.Where(row => row["proposedBy"].AsInt64 == Maker && row["removes"].AsBsonArray.Count == 0 &&
            row["adds"].AsBsonArray.All(id => chosen.Contains(id.AsInt64))))
            await service.DecideAsync(Actor(Checker), proposal["_id"].AsInt64, CatalogPublicSampleDecision.Approve,
                Meta("approve." + proposal["_id"].AsInt64, "SYNTHETIC: contoh produk untuk tamu, tanpa harga; diperiksa orang kedua"), ct);

        var samples = (await database.GetCollection<BsonDocument>("catalogPublicSamples").Find(FilterDefinition<BsonDocument>.Empty).Project(new BsonDocument("_id", 1))
            .ToListAsync(ct)).Select(row => row["_id"].AsInt64).ToHashSet();
        var waiting = (await proposals.Find(new BsonDocument("state", "pending")).ToListAsync(ct)).SelectMany(row => row["families"].AsBsonArray.Select(id => id.AsInt64)).ToHashSet();
        var missing = Missing(chosen, samples, waiting);
        if (missing.Length == 0) return;
        if (samples.Count + missing.Length > Limit) { Console.WriteLine("Sampel tamu: batas " + Limit + " produk tercapai; " + missing.Length + " contoh tidak ditambahkan."); return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", missing))))[..16].ToLowerInvariant();
        var proposed = await service.ProposeAsync(Actor(Maker), [.. missing], [],
            Meta("propose." + key, "SYNTHETIC: " + missing.Length + " produk contoh agar tamu melihat katalog tanpa harga"), ct);
        await service.DecideAsync(Actor(Checker), proposed["proposalId"].AsInt64, CatalogPublicSampleDecision.Approve,
            Meta("approve." + key, "SYNTHETIC: contoh produk untuk tamu, tanpa harga; diperiksa orang kedua"), ct);
        Console.WriteLine("Sampel tamu: " + missing.Length + " produk contoh ditambahkan (diajukan petugas, disetujui peninjau).");
    }

    /// <summary>The chosen products that are neither samples already nor waiting in a proposal, in choice order.</summary>
    public static long[] Missing(IEnumerable<long> chosen, IReadOnlySet<long> samples, IReadOnlySet<long> waiting) =>
        [.. chosen.Where(id => !samples.Contains(id) && !waiting.Contains(id))];
}

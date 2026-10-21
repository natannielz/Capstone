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

    /// <summary>The idempotency-key prefix of the pilot's own proposals; recovery approves only these, never a person's proposal.</summary>
    public const string ProposeKeyPrefix = "local-pilot.samples-v1.propose.";
    private static readonly string[] SkippedConflicts = ["public_sample_limit", "catalog_public_sample_pending", "catalog_public_sample_unchanged",
        "catalog_public_sample_changed", "catalog_public_sample_decided"];

    /// <summary>Runs one sample command; a conflict from a concurrent or changed sample state is logged and skipped (false), so
    /// seeding never fails the pilot start. Anything else still fails.</summary>
    public static async Task<bool> SkipOnConflictAsync(Func<Task> command, string what)
    {
        try { await command(); return true; }
        catch (PersistenceConflictException conflict) when (SkippedConflicts.Contains(conflict.Code))
        {
            Console.WriteLine("Sampel tamu: " + what + " dilewati karena keadaan sampel berubah (" + conflict.Code + "); dicoba lagi saat pilot dimulai ulang.");
            return false;
        }
    }

    public static async Task SeedAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, CancellationToken ct, int limit = Limit)
    {
        var clock = new SystemClock();
        var service = new CatalogPublicSampleService(database, new MongoUnitOfWork(client), new(database), clock);
        // Fresh factor: each actor is built right before its command, as a signed-in person who just completed MFA.
        ActorContext Actor(long id) => new(id, PilotBoundary.Issuer, secrets.Accounts.Single(account => account.Id == id).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        static AuthorizationCommandMetadata Meta(string key, string reason) => new("local-pilot.samples-v1." + key, reason, "local-pilot-samples");
        const string ApproveReason = "SYNTHETIC: contoh produk untuk tamu, tanpa harga; diperiksa orang kedua";
        var sampleRows = database.GetCollection<BsonDocument>("catalogPublicSamples");
        async Task<HashSet<long>> SamplesAsync() =>
            (await sampleRows.Find(FilterDefinition<BsonDocument>.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(row => row["_id"].AsInt64).ToHashSet();

        var codes = ChosenSkus();
        var chosen = new List<long>();
        foreach (var code in codes)
        {
            var sku = await database.GetCollection<BsonDocument>("skus").Find(new BsonDocument { { "sku", code }, { "active", true }, { "catalogListings.assortmentId", 501L } })
                .Project(new BsonDocument("familyId", 1)).FirstOrDefaultAsync(ct);
            if (sku is not null && !chosen.Contains(sku["familyId"].AsInt64)) chosen.Add(sku["familyId"].AsInt64);
        }
        if (chosen.Count != Count) { Console.WriteLine("Sampel tamu: hanya " + chosen.Count + " dari " + Count + " produk contoh tersedia; dilewati."); return; }

        // An interrupted start may have left the pilot's own proposal waiting (found by its key prefix, never by key replay): approve
        // it rather than proposing again. A person's pending proposal, even one made as petugas, is theirs to finish.
        var proposals = database.GetCollection<BsonDocument>("catalogPublicSampleProposals");
        var pending = await proposals.Find(new BsonDocument("state", "pending")).ToListAsync(ct);
        foreach (var proposal in pending.Where(row => row["proposedBy"].AsInt64 == Maker && row["requestKey"].AsString.StartsWith(ProposeKeyPrefix, StringComparison.Ordinal) &&
            row["removes"].AsBsonArray.Count == 0 && row["adds"].AsBsonArray.All(id => chosen.Contains(id.AsInt64))))
        {
            var adding = proposal["adds"].AsBsonArray.Count;
            var held = (await SamplesAsync()).Count;
            if (held + adding > limit) { Console.WriteLine("Sampel tamu: batas " + limit + " produk tercapai; usulan tertunda " + proposal["_id"].AsInt64 + " tidak disetujui."); continue; }
            await SkipOnConflictAsync(() => service.DecideAsync(Actor(Checker), proposal["_id"].AsInt64, CatalogPublicSampleDecision.Approve,
                Meta("approve." + proposal["_id"].AsInt64, ApproveReason), ct), "persetujuan usulan tertunda " + proposal["_id"].AsInt64);
        }

        var samples = await SamplesAsync();
        var waiting = (await proposals.Find(new BsonDocument("state", "pending")).ToListAsync(ct)).SelectMany(row => row["families"].AsBsonArray.Select(id => id.AsInt64)).ToHashSet();
        var missing = Missing(chosen, samples, waiting);
        if (missing.Length == 0) return;
        if (samples.Count + missing.Length > limit) { Console.WriteLine("Sampel tamu: batas " + limit + " produk tercapai; " + missing.Length + " contoh tidak ditambahkan."); return; }
        // Every attempt is its own command: a fresh key, so a set that recurs (removed again later) is proposed again, not replayed.
        long proposalId = 0;
        if (!await SkipOnConflictAsync(async () => proposalId = (await service.ProposeAsync(Actor(Maker), [.. missing], [],
            Meta("propose." + Guid.NewGuid().ToString("N"), "SYNTHETIC: " + missing.Length + " produk contoh agar tamu melihat katalog tanpa harga"), ct))["proposalId"].AsInt64,
            "usulan " + missing.Length + " produk contoh")) return;
        if (!await SkipOnConflictAsync(() => service.DecideAsync(Actor(Checker), proposalId, CatalogPublicSampleDecision.Approve, Meta("approve." + proposalId, ApproveReason), ct),
            "persetujuan usulan " + proposalId)) return;
        Console.WriteLine("Sampel tamu: " + missing.Length + " produk contoh ditambahkan (diajukan petugas, disetujui peninjau).");
    }

    /// <summary>The chosen products that are neither samples already nor waiting in a proposal, in choice order.</summary>
    public static long[] Missing(IEnumerable<long> chosen, IReadOnlySet<long> samples, IReadOnlySet<long> waiting) =>
        [.. chosen.Where(id => !samples.Contains(id) && !waiting.Contains(id))];
}

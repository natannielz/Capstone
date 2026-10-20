using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ImageMagick;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

/// <summary>
/// Product photos for the full demo through the real private-evidence path: a ClamAV daemon on loopback, the pinned isolated
/// image decoder, the private Windows store and the real reserve, upload, validate and associate services. The pilot acts as the
/// local operator for its own SYNTHETIC store (it provisions the store, validation and read policies) and as the catalog officer
/// (it uploads the first demo's photos and requests the rescan the read policy requires at least once a day). Nothing here
/// bypasses a check: a photo a buyer sees has a current clean scan and a full decode of its exact bytes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PilotPhotos : IAsyncDisposable
{
    private const int ClamdPort = 3310;
    private const long MaximumBytes = 2 * 1024 * 1024, HeldReservations = 5000;
    private const string ValidationConsumer = "local-pilot.catalog-photos";
    private const string RescanConsumer = "local-pilot.catalog-photo-rescan";
    // Operator classes the private store accepts: this Windows user, SYSTEM, Administrators and TrustedInstaller (owner of C:\).
    private static readonly string[] TrustedSids = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
    private static readonly TimeSpan RescanAge = TimeSpan.FromHours(12), SignatureRefreshAge = TimeSpan.FromHours(20);

    private sealed record Installation(string StoreId, DateTimeOffset InstalledAt, string Executable, string Assembly, string ExecutableSha256,
        string AssemblySha256, string DepsSha256, string RuntimeConfigSha256, string ManagedLibrarySha256, string CoreLibrarySha256,
        string NativeLibrarySha256, string PolicySha256);

    private readonly string repository, directory, clamav;
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly PilotSecrets secrets;
    private readonly SystemClock clock = new();
    private readonly CatalogPhotoWorkflow workflow;
    private readonly EvidenceValidationRuntime validation;
    private readonly EvidenceImageRevalidationRuntime rescan;
    private Process? daemon;

    public IReadOnlyDictionary<string, string?> ApiSettings { get; }

    private PilotPhotos(string repository, string directory, string clamav, IMongoDatabase database, MongoClient client, PilotSecrets secrets,
        Process? daemon, EvidenceValidationRuntimeOptions options, EvidenceImageRevalidationOptions rescanOptions)
    {
        this.repository = repository; this.directory = directory; this.clamav = clamav; this.database = database; this.secrets = secrets; this.daemon = daemon;
        unit = new MongoUnitOfWork(client);
        validation = EvidenceValidationRuntime.Create(database, unit, new(database), clock, options);
        rescan = EvidenceImageRevalidationRuntime.Create(database, unit, new(database), clock, rescanOptions);
        workflow = new CatalogPhotoWorkflow(database, unit, new(database), clock, new CatalogAuthoringService(database, unit, new(database), clock),
            new CatalogPhotoStorageOptions(options.Root, options.StoreId, options.TrustedOperatorSids));
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Evidence:PrivateWindows:Root"] = options.Root, ["Evidence:PrivateWindows:StoreId"] = options.StoreId,
        };
        for (var index = 0; index < options.TrustedOperatorSids.Length; index++) settings["Evidence:PrivateWindows:TrustedOperatorSids:" + index] = options.TrustedOperatorSids[index];
        Flatten(JsonSerializer.SerializeToElement(rescanOptions), "Evidence:ImageRevalidation", settings);
        ApiSettings = settings;
    }

    /// <summary>Null when ClamAV is not installed under .tools/clamav: the demo then runs without product photos.</summary>
    public static async Task<PilotPhotos?> StartAsync(string repository, string stateDirectory, IMongoDatabase database, MongoClient client,
        PilotSecrets secrets, CancellationToken ct)
    {
        var clamav = Directory.Exists(Path.Combine(repository, ".tools", "clamav"))
            ? Directory.GetDirectories(Path.Combine(repository, ".tools", "clamav"), "clamav-*.win.x64").Where(path => File.Exists(Path.Combine(path, "clamd.exe")))
                .OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (clamav is null) { Console.WriteLine("Foto produk: ClamAV belum terpasang di .tools/clamav; katalog berjalan tanpa foto."); return null; }
        var engine = Path.GetFileName(clamav)["clamav-".Length..^".win.x64".Length];
        var home = Path.Combine(stateDirectory, "photos");
        PilotBoundary.ProtectDirectory(home);
        var signatures = Path.Combine(home, "clamav-db");
        PilotBoundary.ProtectDirectory(signatures);
        WriteClamavConfiguration(home, signatures);
        await RefreshSignaturesAsync(clamav, home, signatures, ct);
        var daemon = await StartDaemonAsync(clamav, home, engine, ct);
        try
        {
            var installation = Install(home);
            // The store refuses a root whose ancestors grant write access to anyone outside the trusted operators, and checkout
            // folders may carry such grants from other tools. The per-user application data folder carries none.
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnitToko.LocalPilot", PilotBoundary.Profile);
            PilotBoundary.ProtectDirectory(Path.GetDirectoryName(data)!);
            PilotBoundary.ProtectDirectory(data);
            var root = Path.Combine(data, "evidence");
            CreatePrivateRoot(root);
            var limits = new EvidenceLimits(MaximumBytes, 4096, 4096, 16_000_000);
            var decoder = new ImageDecoderOptions(installation.Executable, installation.Assembly, installation.ExecutableSha256, installation.AssemblySha256,
                installation.DepsSha256, installation.RuntimeConfigSha256, installation.ManagedLibrarySha256, installation.CoreLibrarySha256,
                installation.NativeLibrarySha256, installation.PolicySha256, 256 * 1024 * 1024, TimeSpan.FromSeconds(10));
            var scanner = new LocalClamdOptions(ClamdPort, engine, 28000, TimeSpan.FromDays(7), MaximumBytes, TimeSpan.FromSeconds(30));
            var acceptance = "SYNTHETIC local pilot: ClamAV " + engine + " on loopback, decoder copy pinned " +
                installation.InstalledAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "; not a production host acceptance";
            var options = new EvidenceValidationRuntimeOptions(root, installation.StoreId, TrustedSids, ValidationConsumer, acceptance, limits, decoder, scanner);
            var rescanOptions = new EvidenceImageRevalidationOptions(root, installation.StoreId, TrustedSids, RescanConsumer, acceptance, limits, decoder, scanner);
            var photos = new PilotPhotos(repository, stateDirectory, clamav, database, client, secrets, daemon, options, rescanOptions);
            await photos.ProvisionAsync(installation.StoreId, acceptance, ct);
            return photos;
        }
        catch { Stop(daemon); throw; }
    }

    /// <summary>The worker loop, the one-time photo upload and the daily rescan, until the pilot stops.</summary>
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(WorkAsync(ct), MaintainAsync(ct));

    private async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var busy = false;
            try
            {
                busy |= await validation.RunOnceAsync(ct) is "completed" or "retry";
                busy |= await rescan.RunOnceAsync(ct) is "completed" or "retry";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exception) { Console.WriteLine("Foto produk: pemeriksaan tertunda (" + exception.GetType().Name + ")."); }
            if (!busy) try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        try
        {
            await SeedAsync(ct);
            await CheckBuyerReadsAsync(ct);
            var signaturesChecked = clock.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                if (clock.UtcNow - signaturesChecked > SignatureRefreshAge)
                {
                    await RefreshSignaturesAsync(clamav, Path.Combine(directory, "photos"), Path.Combine(directory, "photos", "clamav-db"), ct);
                    signaturesChecked = clock.UtcNow;
                }
                await RescanAsync(ct);
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { Console.WriteLine("Foto produk berhenti: " + exception.Message); }
    }

    /// <summary>Reads three photos the way a requester's browser does, so the start-up log says whether buyers see photos and how fast.</summary>
    private async Task CheckBuyerReadsAsync(CancellationToken ct)
    {
        var buyer = new ActorContext(1001, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 1001).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        var skus = database.GetCollection<BsonDocument>("skus");
        var read = 0; var timer = Stopwatch.StartNew();
        foreach (var (product, skuId) in (await ProductsAsync(ct)).Take(3))
        {
            var row = await skus.Find(new BsonDocument("_id", skuId)).Project(new BsonDocument("publicId", 1)).SingleAsync(ct);
            try
            {
                await using var photo = await workflow.OpenBuyerAsync(buyer, row["publicId"].AsString,
                    new AuthorizationReadMetadata("catalog-photo-current", "local-pilot-buyer-check"), ct);
                if (photo is null) continue;
                await photo.Content.CopyToAsync(Stream.Null, ct);
                read++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine("Foto " + product.Sku + ": pemohon belum dapat melihatnya (" + Describe(exception) + ").");
            }
        }
        Console.WriteLine("Foto produk: pemohon dapat melihat " + read + "/3 foto contoh (rata-rata " + timer.ElapsedMilliseconds / 3 + " ms per foto).");
    }

    private ActorContext Officer() =>
        new(2001, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 2001).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
    private static AuthorizationCommandMetadata Meta(string key, string reason) => new(key, reason, "local-pilot-photos");

    private async Task<List<(PilotCatalog.DemoProduct Product, long SkuId)>> ProductsAsync(CancellationToken ct)
    {
        var skus = database.GetCollection<BsonDocument>("skus");
        var products = new List<(PilotCatalog.DemoProduct, long)>();
        foreach (var product in PilotCatalog.DemoProducts())
        {
            var row = await skus.Find(new BsonDocument("sku", product.Sku)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            if (row is not null) products.Add((product, row["_id"].AsInt64));
        }
        return products;
    }

    /// <summary>The first demo's photos, once: each one reserved, uploaded, scanned, decoded and associated like an officer's upload.</summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        var markers = database.GetCollection<BsonDocument>("localPilotMetadata");
        var marker = await markers.Find(new BsonDocument("_id", PilotBoundary.Profile)).SingleAsync(ct);
        if (marker.GetValue("catalogPhotos", 0).ToInt32() >= 1) return;
        var products = await ProductsAsync(ct);
        int added = 0, failed = 0;
        Console.WriteLine("Foto produk: mengunggah " + products.Count + " foto contoh melalui pemindaian ClamAV dan dekoder (sekali, beberapa menit).");
        foreach (var (product, skuId) in products)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(repository, "frontend", "public", product.Image.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { failed++; continue; }
            try { if (await AddPhotoAsync(product.Sku, skuId, file, ct)) added++; else failed++; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                Console.WriteLine("Foto " + product.Sku + " gagal: " + Describe(exception));
            }
            if ((added + failed) % 25 == 0) Console.WriteLine("Foto produk: " + (added + failed) + "/" + products.Count + " diproses.");
            if (added == 0 && failed >= 5) { Console.WriteLine("Foto produk: lima foto pertama gagal; unggahan dihentikan sampai pilot dimulai ulang."); break; }
        }
        Console.WriteLine("Foto produk: " + added + " foto aktif" + (failed > 0 ? ", " + failed + " gagal (lihat di atas)" : "") + ".");
        if (failed == 0) await markers.UpdateOneAsync(new BsonDocument("_id", PilotBoundary.Profile), Builders<BsonDocument>.Update.Set("catalogPhotos", 1), cancellationToken: ct);
    }

    private async Task<bool> AddPhotoAsync(string code, long skuId, string file, CancellationToken ct)
    {
        var current = await workflow.CurrentAsync(Officer(), skuId, ct);
        if (current.Photo is not null) return true;
        var bytes = Jpeg(file);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var key = "local-pilot.photos-v1." + code + ".a" + attempt;
            // A replayed key returns its original reservation; one that expired before its upload is skipped for a new key.
            var reservation = await workflow.ReserveAsync(Officer(), skuId, current.SkuRevision,
                Meta(key + ".reserve", "SYNTHETIC demo product photo from the first demo"), ct);
            var status = await workflow.StatusAsync(Officer(), skuId, reservation.ReservationId, ct);
            if (status.State == "fresh")
            {
                if (status.ExpiresAt <= clock.UtcNow.AddSeconds(30)) continue;
                using var original = new MemoryStream(bytes);
                await workflow.UploadAsync(Officer(), skuId, reservation.ReservationId, status.Revision, original,
                    Meta(key + ".upload", "SYNTHETIC demo product photo from the first demo"), ct);
            }
            else if (status.State != "quarantined") continue;
            // The worker loop scans and decodes the quarantined original.
            var deadline = clock.UtcNow.AddSeconds(90);
            do
            {
                status = await workflow.StatusAsync(Officer(), skuId, reservation.ReservationId, ct);
                if (status.State != "quarantined" || status.ScanStatus != "unavailable" || status.SafeFailure is not null) break;
                await Task.Delay(400, ct);
            }
            while (clock.UtcNow < deadline);
            if (status.State == "quarantined" && status.ScanStatus == "clean" && status.Reference is { } reference && status.SafeFailure is null)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    await workflow.AssociateAsync(Officer(), new CatalogAssociatePrimaryPhoto(new LongId(skuId), current.SkuRevision, current.PhotoRevision,
                    reservation.ReservationId, reference), Meta(key + ".associate", "SYNTHETIC demo product photo from the first demo"), ct);
                }
                catch (PersistenceConflictException exception) when (exception.Code == "catalog_photo_upload_again")
                {
                    // The store or approval policy changed after this upload was admitted; a new upload is the only way on.
                    continue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.WriteLine("Foto " + code + ": menautkan gagal setelah " + timer.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s (" + Describe(exception) + ").");
                    throw;
                }
                if (timer.Elapsed > TimeSpan.FromSeconds(3))
                    Console.WriteLine("Foto " + code + ": menautkan butuh " + timer.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
                return true;
            }
            Console.WriteLine("Foto " + code + ": " + status.State + "/" + status.ScanStatus + "/" + status.DecoderStatus + (status.SafeFailure is null ? "" : "/" + status.SafeFailure));
            if (status.ScanStatus is "infected") return false;
        }
        return false;
    }

    /// <summary>Requests a fresh scan of every photo whose current verdict is older than twelve hours; buyers see a photo only while
    /// its verdict is under a day old.</summary>
    private async Task RescanAsync(CancellationToken ct)
    {
        var requested = 0;
        foreach (var (product, skuId) in await ProductsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var current = await workflow.CurrentAsync(Officer(), skuId, ct);
                if (current.Photo is not { MatchesCurrentDefinition: true } photo) continue;
                var proof = await rescan.CurrentCatalogImageAsync(Officer(), skuId, photo.ReservationId, photo.Reference, ct);
                // Before any rescan the association time stands in for the first verdict, which was checked seconds earlier.
                if (proof.State == "pending" || clock.UtcNow - (proof.CheckedAt ?? photo.RecordedAt) < RescanAge) continue;
                var hour = clock.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                await rescan.RequestCatalogImageAsync(Officer(), new RequestCatalogImageRevalidation(new LongId(skuId), current.SkuRevision,
                    proof.ProofRevision, photo.ReservationId, photo.Reference),
                    Meta("local-pilot.photo-rescan." + product.Sku + "." + hour + "." + proof.ProofRevision, "SYNTHETIC daily rescan of a demo product photo"), ct);
                requested++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine("Pindai ulang " + product.Sku + " gagal: " + Describe(exception));
            }
        }
        if (requested > 0) Console.WriteLine("Foto produk: " + requested + " foto dipindai ulang.");
    }

    /// <summary>Baseline JPEG, 800 px, no metadata: the decoder accepts only clean single-frame PNG and JPEG.</summary>
    private static byte[] Jpeg(string file)
    {
        using var image = new MagickImage(file);
        image.Strip();
        image.Resize(800, 800);
        image.ColorSpace = ColorSpace.sRGB;
        image.Format = MagickFormat.Jpeg;
        image.Quality = 84;
        return image.ToByteArray();
    }

    private async Task ProvisionAsync(string storeId, string acceptance, CancellationToken ct)
    {
        var now = clock.UtcNow.UtcDateTime;
        var ids = new IdAllocator(database);
        var stores = database.GetCollection<BsonDocument>("evidenceStorePolicies");
        if (!await stores.Find(new BsonDocument("storeId", storeId)).AnyAsync(ct))
            await stores.InsertOneAsync(new BsonDocument
            {
                { "_id", (await ids.NextAsync("evidenceStorePolicies", ct)).Value }, { "schemaVersion", 1 }, { "revision", 1L },
                { "createdAt", now }, { "updatedAt", now }, { "storeId", storeId }, { "active", true }, { "policyVersion", 1L },
                { "maximumContentBytes", MaximumBytes }, { "maximumHeldReservations", HeldReservations }, { "maximumHeldBytes", HeldReservations * 3 * MaximumBytes },
                { "reservationDurationMs", 600000L }, { "writerDurationMs", 120000L }, { "ioDurationMs", 30000L },
                { "bufferBytes", 65536L }, { "chargedReservations", 0L }, { "chargedBytes", 0L }, { "fence", 0L }
            }, cancellationToken: ct);
        else
            // Every reservation the store has held counts against these limits (each one charges three times the content limit).
            await stores.UpdateOneAsync(new BsonDocument { { "storeId", storeId }, { "maximumHeldReservations", new BsonDocument("$lt", HeldReservations) } },
                Builders<BsonDocument>.Update.Set("maximumHeldReservations", HeldReservations).Set("maximumHeldBytes", HeldReservations * 3 * MaximumBytes)
                    .Set("updatedAt", now).Inc("revision", 1L), cancellationToken: ct);
        var reads = database.GetCollection<BsonDocument>("evidenceReadPolicies");
        if (!await reads.Find(new BsonDocument("storeId", storeId)).AnyAsync(ct))
            await reads.InsertOneAsync(new BsonDocument
            {
                { "_id", (await ids.NextAsync("evidenceReadPolicies", ct)).Value }, { "schemaVersion", 1 }, { "storeId", storeId },
                { "active", true }, { "revision", 1L }, { "fence", 0L }, { "createdAt", now }, { "updatedAt", now },
                { "maximumBytes", MaximumBytes }, { "verificationDurationMs", 30000L }, { "leaseDurationMs", 10000L },
                { "requestDurationMs", 120000L }, { "maximumValidationAgeMs", 86400000L }, { "maximumSignatureAgeMs", 604800000L }
            }, cancellationToken: ct);
        else
            // The longest windows the read policy allows: associating a photo verifies its bytes and re-proves the catalog owner
            // inside one lease, which takes longer than five seconds on a busy single machine.
            await reads.UpdateOneAsync(new BsonDocument { { "storeId", storeId }, { "leaseDurationMs", new BsonDocument("$lt", 10000L) } },
                Builders<BsonDocument>.Update.Set("verificationDurationMs", 30000L).Set("leaseDurationMs", 10000L).Set("requestDurationMs", 120000L)
                    .Set("updatedAt", now).Inc("revision", 1L), cancellationToken: ct);
        await ApprovalAsync("evidenceValidationPolicies", ValidationConsumer, validation.PipelineId, storeId, acceptance, ct);
        await ApprovalAsync("evidenceImageRevalidationPolicies", RescanConsumer, rescan.PipelineId, storeId, acceptance, ct);
    }

    /// <summary>The worker approval for this exact pipeline. A changed pipeline (new ClamAV or decoder copy) gets a new revision; a
    /// current approval is extended before it lapses. Earlier verdicts stay bound to the revision they were checked under.</summary>
    private async Task ApprovalAsync(string collection, string consumer, string pipeline, string storeId, string acceptance, CancellationToken ct)
    {
        var rows = database.GetCollection<BsonDocument>(collection);
        var now = clock.UtcNow.UtcDateTime;
        var existing = await rows.Find(new BsonDocument("consumerId", consumer)).SingleOrDefaultAsync(ct);
        if (existing is null)
        {
            await rows.InsertOneAsync(new BsonDocument
            {
                { "_id", (await new IdAllocator(database).NextAsync(collection, ct)).Value }, { "schemaVersion", 1 }, { "consumerId", consumer },
                { "storeId", storeId }, { "pipelineId", pipeline }, { "active", true }, { "revision", 1L }, { "fence", 0L },
                { "approvedAt", now.AddMinutes(-1) }, { "expiresAt", now.AddDays(30) }, { "hostAcceptanceRef", acceptance }
            }, cancellationToken: ct);
            return;
        }
        if (existing["pipelineId"] != pipeline || existing["storeId"] != storeId)
            await rows.UpdateOneAsync(new BsonDocument("_id", existing["_id"]), Builders<BsonDocument>.Update
                .Set("pipelineId", pipeline).Set("storeId", storeId).Set("active", true).Inc("revision", 1L)
                .Set("approvedAt", now.AddMinutes(-1)).Set("expiresAt", now.AddDays(30)).Set("hostAcceptanceRef", acceptance), cancellationToken: ct);
        else if (existing["expiresAt"].ToUniversalTime() < now.AddDays(7))
            await rows.UpdateOneAsync(new BsonDocument("_id", existing["_id"]), Builders<BsonDocument>.Update.Set("expiresAt", now.AddDays(30)), cancellationToken: ct);
    }

    /// <summary>A private copy of the built decoder, pinned when it is installed. Later rebuilds do not change it; delete the
    /// photos/decoder folder (and its installation file) to install a newer decoder deliberately.</summary>
    private static Installation Install(string home)
    {
        var file = Path.Combine(home, "installation.json");
        if (File.Exists(file)) return JsonSerializer.Deserialize<Installation>(File.ReadAllText(file)) ?? throw new InvalidOperationException("Invalid photo installation.");
        var source = Path.Combine(AppContext.BaseDirectory, "ImageDecoder");
        if (!File.Exists(Path.Combine(source, "UnitToko.ImageDecoder.dll"))) throw new InvalidOperationException("Build the local pilot so its image decoder copy exists.");
        var target = Path.Combine(home, "decoder");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(path, copy);
        }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The .NET executable path is unavailable.");
        if (!Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Start the pilot with dotnet.exe.");
        var assembly = Path.Combine(target, "UnitToko.ImageDecoder.dll");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var installation = new Installation(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), DateTimeOffset.UtcNow, executable, assembly,
            Hash(executable), Hash(assembly), Hash(Path.ChangeExtension(assembly, ".deps.json")), Hash(Path.ChangeExtension(assembly, ".runtimeconfig.json")),
            Hash(Path.Combine(target, "Magick.NET-Q8-AnyCPU.dll")), Hash(Path.Combine(target, "Magick.NET.Core.dll")),
            Hash(Path.Combine(target, "runtimes", "win-x64", "native", "Magick.Native-Q8-x64.dll")), Hash(Path.Combine(target, "policy.xml")));
        File.WriteAllText(file, JsonSerializer.Serialize(installation, new JsonSerializerOptions { WriteIndented = true }));
        return installation;
    }

    /// <summary>The store root: owned by this user, inheritance cut, full control only for this user, SYSTEM and Administrators.</summary>
    private static void CreatePrivateRoot(string root)
    {
        if (Directory.Exists(root)) return;
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows user is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(security);
    }

    private static void WriteClamavConfiguration(string home, string signatures)
    {
        File.WriteAllLines(Path.Combine(home, "freshclam.conf"), [
            "DatabaseDirectory " + signatures, "DatabaseMirror database.clamav.net", "UpdateLogFile " + Path.Combine(home, "freshclam.log"),
            "LogTime yes", "ConnectTimeout 30", "ReceiveTimeout 600"]);
        // Loopback only; StreamMaxLength covers the 2 MiB photo limit with room for the INSTREAM framing.
        File.WriteAllLines(Path.Combine(home, "clamd.conf"), [
            "TCPSocket " + ClamdPort, "TCPAddr 127.0.0.1", "DatabaseDirectory " + signatures, "LogFile " + Path.Combine(home, "clamd.log"),
            "LogTime yes", "StreamMaxLength 10M", "MaxThreads 2", "MaxQueue 20", "Foreground yes"]);
    }

    /// <summary>ClamAV's own updater, when the daily signatures are older than twenty hours. A failed update keeps the existing
    /// signatures; the scanner refuses them once they are a week old.</summary>
    private static async Task RefreshSignaturesAsync(string clamav, string home, string signatures, CancellationToken ct)
    {
        var daily = new[] { "daily.cld", "daily.cvd" }.Select(name => new FileInfo(Path.Combine(signatures, name))).Where(info => info.Exists)
            .Select(info => info.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var freshness = Path.Combine(home, "signatures-checked.txt");
        var checkedAt = File.Exists(freshness) && DateTime.TryParse(File.ReadAllText(freshness), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : daily;
        if (daily != DateTime.MinValue && DateTime.UtcNow - checkedAt < SignatureRefreshAge) return;
        Console.WriteLine("Foto produk: memperbarui tanda tangan virus ClamAV.");
        var start = new ProcessStartInfo(Path.Combine(clamav, "freshclam.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--config-file=" + Path.Combine(home, "freshclam.conf"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start freshclam.");
        _ = process.StandardOutput.ReadToEndAsync(ct); _ = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { process.Kill(true); }
        if (process.HasExited && process.ExitCode == 0) File.WriteAllText(freshness, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        else Console.WriteLine("Foto produk: pembaruan tanda tangan gagal; tanda tangan yang ada tetap dipakai.");
        if (!File.Exists(Path.Combine(signatures, "main.cvd")) && !File.Exists(Path.Combine(signatures, "main.cld")))
            throw new InvalidOperationException("ClamAV signatures are unavailable; connect once so freshclam can download them.");
    }

    /// <summary>clamd on loopback with TZ=UTC, so its version reply carries the UTC signature time the scanner requires. An
    /// instance this pilot left running earlier is reused when it reports the same engine.</summary>
    private static async Task<Process?> StartDaemonAsync(string clamav, string home, string engine, CancellationToken ct)
    {
        if (await VersionAsync(ct) is { } running)
        {
            if (running.StartsWith("ClamAV " + engine + "/", StringComparison.Ordinal)) return null;
            throw new InvalidOperationException("Port " + ClamdPort + " is serving another scanner (" + running + ").");
        }
        var start = new ProcessStartInfo(Path.Combine(clamav, "clamd.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--config-file=" + Path.Combine(home, "clamd.conf"));
        start.Environment["TZ"] = "UTC";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start clamd.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("clamd stopped during startup; inspect photos/clamd.log.");
                if (await VersionAsync(deadline.Token) is not null) return process;
                await Task.Delay(500, deadline.Token);
            }
        }
        catch { Stop(process); throw; }
    }

    private static async Task<string?> VersionAsync(CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, ClamdPort, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zVERSION\0"u8.ToArray(), timeout.Token);
            var buffer = new byte[512]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
                if (buffer[count - 1] == 0) break;
            }
            return count > 0 ? Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\0') : null;
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    private static string Describe(Exception exception) => exception switch
    {
        PersistenceConflictException conflict => conflict.Code,
        DomainValidationException invalid => invalid.Code,
        _ => exception.GetType().Name + " " + exception.Message,
    };

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> settings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: foreach (var property in element.EnumerateObject()) Flatten(property.Value, prefix + ":" + property.Name, settings); break;
            case JsonValueKind.Array: var index = 0; foreach (var item in element.EnumerateArray()) Flatten(item, prefix + ":" + index++, settings); break;
            case JsonValueKind.String: settings[prefix] = element.GetString(); break;
            default: settings[prefix] = element.GetRawText(); break;
        }
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch (InvalidOperationException) { }
        process.Dispose();
    }

    public ValueTask DisposeAsync() { Stop(daemon); daemon = null; return ValueTask.CompletedTask; }
}

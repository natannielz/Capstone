using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Catalog;

/// <summary>Local synthetic provider/scanner inputs; real session, private store, native decoder, HTTP and Mongo owners.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class CatalogPhotoBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Staff_uploads_reviews_recovers_and_replaces_real_private_photo_while_buyer_reads_current_bytes_and_foreign_scope_is_denied()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        data.Catalog.Clock.UseSystemClock = true;
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Actor, new(new(data.Target.Id), 1, [new(501)], [new(new(501), 1)]),
            CatalogAuthoringTestData.Metadata("synthetic-photo-browser-publication"));
        var sku = await data.RowAsync("skus", data.Target.Id);
        var family = await data.RowAsync("productFamilies", sku["familyId"].AsInt64);
        var definition = await data.RowAsync("variantDefinitions", sku["currentDefinitionId"].AsInt64);
        var first = ImageDecoderTests.Png(13, 7); var second = ImageDecoderTests.Png(17, 9);
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var firstHash = Hash(first); var secondHash = Hash(second); Assert.NotEqual(firstHash, secondHash);
        var scanner = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        var processor = EvidenceValidationTests.Processor(data, scanner);
        await EvidenceValidationTests.Policy(data, processor.PipelineId);
        const string rescanConsumer = "synthetic-catalog-photo-browser-rescanner";
        var rescanPipeline = new string('d', 64);
        var rescanScanner = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        var rescanRequests = new EvidenceImageRevalidationService(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Registration, data.Catalog.Service, rescanConsumer, rescanPipeline);
        var rescanProcessor = new EvidenceImageRevalidationProcessor(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Registration, rescanConsumer, rescanPipeline, new(1024, 100, 100, 10000), rescanScanner,
            new WindowsImageDecoder(ImageDecoderTests.Options()));
        var rescanRuntime = new EvidenceImageRevalidationRuntime(rescanRequests, rescanProcessor,
            new CollectionMigrations(data.DB, data.Catalog.Clock), rescanPipeline);
        var approvalAt = data.Catalog.Clock.UtcNow;
        await data.Rows(EvidenceImageRevalidationSchemas.Policies).InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync(EvidenceImageRevalidationSchemas.Policies)).Value },
            { "schemaVersion", 1 }, { "consumerId", rescanConsumer }, { "storeId", data.StoreId },
            { "pipelineId", rescanPipeline }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "approvedAt", approvalAt.AddMinutes(-1).UtcDateTime }, { "expiresAt", approvalAt.AddMinutes(10).UtcDateTime },
            { "hostAcceptanceRef", "synthetic-browser-only-not-production-acceptance" } });
        await data.Rows("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync("evidenceReadPolicies")).Value },
            { "schemaVersion", 1 }, { "storeId", data.StoreId }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "createdAt", data.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime },
            { "maximumBytes", 1024L }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L },
            { "requestDurationMs", 60000L }, { "maximumValidationAgeMs", 86400000L }, { "maximumSignatureAgeMs", 604800000L } });
        async Task<BsonDocument> UnchangedAsync() {
            var state = new BsonDocument();
            foreach (var table in new[] { "catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers",
                "catalogIdentifierHistory", "catalogAssortments", "catalogEntitlements", "priceVersions", "orders", "orderDrafts",
                "stockBalances", "lots", "movements", "reservations", "invoices", "payments", "paymentAllocations",
                "paymentReceiptBalances", "paymentInvoiceBalances", "bankReceipts", "journalEntries" }) {
                var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) row.Remove("fence"); state[table] = new BsonArray(rows);
            }
            return state;
        }
        var before = await UnchangedAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.DB.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var workflow = new CatalogPhotoWorkflow(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Catalog.Service, data.Registration);
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Catalog.Clock);
            services.RemoveAll<CatalogPhotoWorkflow>(); services.AddSingleton(workflow);
            services.RemoveAll<EvidenceImageRevalidationRuntime>(); services.AddSingleton(rescanRuntime);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        foreach (var (actor, label) in new[] { (data.Actor, "staff"), (data.Catalog.Buyer, "buyer"), (data.Catalog.Other, "outsider") }) {
            var subject = "auth0|synthetic-photo-browser-" + label; var now = data.Catalog.Clock.UtcNow;
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update
                .Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
                { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Akun foto SINTETIS " + label },
                { "verifiedEmail", label + "@example.invalid" }, { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, label + "@example.invalid", true,
                configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
            var sessions = factory.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-photo-browser-provider", ct));
            tokens[label] = issued.OpaqueToken;
        }
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko private catalog photograph rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(configured.PublicOrigin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var spki = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        var directory = Path.Combine(root, "tmp", "production", "catalog-photo-browser-results", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "native-child.log");
        static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        var input = new { apiAddress, origin = configured.PublicOrigin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, spki,
            playwrightPackage = playwright, tokens, directory, staffId = Id(data.Actor.UserId), buyerId = Id(data.Catalog.Buyer.UserId), outsiderId = Id(data.Catalog.Other.UserId),
            skuId = Id(data.Target.Id), skuCode = sku["sku"].AsString, publicId = sku["publicId"].AsString, familyName = family["name"].AsString,
            packageLabel = definition["packageLabel"].AsString, firstPng = Convert.ToBase64String(first), secondPng = Convert.ToBase64String(second), firstHash, secondHash };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-catalog-photo-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Catalog photo browser child did not start");
        var lines = new StringBuilder(); var validations = 0; var revalidations = 0; var elapsed = Stopwatch.StartNew();
        byte[]? firstValidationBytes = null;
        async Task<byte[]> ValidationSnapshotAsync() => new BsonDocument("rows", new BsonArray(
            await data.Rows("evidenceValidationResults").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync())).ToBson();
        async Task ReadAsync(StreamReader stream, bool canValidate) {
            while (await stream.ReadLineAsync() is { } line) {
                lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
                if (canValidate && line == "CATALOG_PHOTO_VALIDATE_ORIGINAL") {
                    Assert.InRange(++validations, 1, 2);
                    // This is the actual outbox worker after an actual HTTP upload. Only its scanner is a named synthetic fixture.
                    Assert.Equal("completed", await processor.RunOnceAsync());
                }
                if (canValidate && line == "CATALOG_PHOTO_REVALIDATE_ORIGINAL") {
                    Assert.Equal(1, ++revalidations);
                    firstValidationBytes = await ValidationSnapshotAsync();
                    var pendingHead = await data.Rows(EvidenceImageRevalidationSchemas.Heads).Find(new BsonDocument()).SingleAsync();
                    Assert.Equal("pending", pendingHead["status"]);
                    Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
                    Assert.Equal("completed", await rescanRuntime.RunOnceAsync());
                    Assert.Equal(firstValidationBytes, await ValidationSnapshotAsync());
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput, true); var stderr = ReadAsync(process.StandardError, false);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            var firstCompletion = await Task.WhenAny(exit, stdout, stderr); if (firstCompletion.IsFaulted) await firstCompletion;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_CATALOG_PHOTO_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); }
            finally { output.WriteLine($"Actual catalog photo browser: {elapsed.Elapsed.TotalSeconds:F3}s"); output.WriteLine("Browser artifacts: " + directory); }
        }
        Assert.Equal(2, validations); Assert.Equal(2, scanner.Calls);
        Assert.Equal(1, revalidations); Assert.Equal(1, rescanScanner.Calls);
        Assert.NotNull(firstValidationBytes);
        Assert.Equal(firstValidationBytes, await ValidationSnapshotAsync());
        var photos = await data.Rows(CatalogPhotoRecords.Photos).Find(new BsonDocument()).Sort(new BsonDocument("photoRevision", 1)).ToListAsync();
        Assert.Equal(2, photos.Count); Assert.Equal(new[] { firstHash, secondHash }, photos.Select(row => row["reference"]["sha256"].AsString));
        Assert.Equal(new long[] { 1, 2 }, photos.Select(row => row["photoRevision"].AsInt64));
        Assert.True(photos[0]["previousPhotoId"].IsBsonNull); Assert.Equal(photos[0]["_id"], photos[1]["previousPhotoId"]);
        Assert.All(photos, row => { Assert.Equal(data.Target.Id, row["skuId"].AsInt64); Assert.Equal(data.Actor.UserId, row["actorId"].AsInt64);
            Assert.Equal(sku["currentDefinitionId"], row["definitionId"]); Assert.Equal("image/png", row["mime"].AsString); });
        var head = await data.Rows(CatalogPhotoRecords.Heads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2L, head["revision"].AsInt64); Assert.Equal(photos[1]["_id"], head["currentPhotoId"]); Assert.Equal(photos[1]["snapshotHash"], head["photoHash"]);
        Assert.Equal(2L, await data.Rows(CatalogPhotoRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows(CatalogPhotoRecords.Audits).CountDocumentsAsync(new BsonDocument()));
        foreach (var photo in photos) {
            CatalogPhotoRecords.RequirePhoto(photo); Assert.Equal(CatalogPhotoRecords.Hash(photo), photo["snapshotHash"].AsString);
            var receipt = await data.Rows(CatalogPhotoRecords.Receipts).Find(new BsonDocument("photoId", photo["_id"])).SingleAsync();
            var audit = await data.Rows(CatalogPhotoRecords.Audits).Find(new BsonDocument("photoId", photo["_id"])).SingleAsync();
            Assert.Equal(photo["actorId"], receipt["actorId"]); Assert.Equal(photo["requestKey"], receipt["key"]);
            Assert.Equal(photo["bodyHash"], receipt["bodyHash"]); Assert.Equal(photo["_id"], receipt["result"]["associationId"]);
            Assert.Equal(photo["skuId"], receipt["result"]["skuId"]); Assert.Equal(photo["photoRevision"], receipt["result"]["photoRevision"]);
            Assert.Equal(audit["_id"], receipt["auditId"]); Assert.Equal(photo["snapshotHash"], audit["snapshotHash"]);
            Assert.Equal(photo["bodyHash"], audit["bodyHash"]); Assert.Equal(photo["actorId"], audit["actorId"]);
        }
        Assert.Equal(2L, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("documentVersions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument("status", "passed")));
        Assert.Equal(2L, await data.Rows("evidenceValidationAudits").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(EvidenceImageRevalidationSchemas.Requests).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(EvidenceImageRevalidationSchemas.Receipts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(EvidenceImageRevalidationSchemas.Audits).CountDocumentsAsync(new BsonDocument()));
        var rescanHead = await data.Rows(EvidenceImageRevalidationSchemas.Heads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(photos[1]["versionRowId"], rescanHead["_id"]);
        Assert.Equal(1L, rescanHead["revision"]); Assert.Equal("passed", rescanHead["status"]);
        var rescanJob = await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("completed", rescanJob["status"]); Assert.Equal(1, rescanJob["attempts"]);
        var reads = await data.Rows("evidenceReadAudits").Find(new BsonDocument()).ToListAsync();
        Assert.NotEmpty(reads); Assert.DoesNotContain(reads, row => row["actorId"].AsInt64 == data.Catalog.Other.UserId);
        Assert.Equal(before, await UnchangedAsync());
    }

    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

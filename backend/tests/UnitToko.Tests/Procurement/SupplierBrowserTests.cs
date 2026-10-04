using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using D = UnitToko.Tests.Procurement.SupplierTestData;

namespace UnitToko.Tests.Procurement;

/// <summary>Actual production Next/Kestrel/Mongo with explicitly synthetic people and provider sessions.</summary>
[Collection("Mongo")]
public sealed class SupplierBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_proposes_real_supplier_mapping_and_independently_observes_and_approves_with_original_key_recovery() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native browser rehearsal requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await D.CreateAsync(fixture); data.Clock.UseSystemClock = true;
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock,
            new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.Outsider }) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = data.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "supplier-" + actor.UserId + "@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-supplier-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic supplier participant " + actor.UserId }, { "verifiedEmail", "supplier-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var original = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", D.Policies }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); original[table] = new BsonArray(rows);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<SupplierService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko supplier governance rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var minute = DateTimeOffset.FromUnixTimeSeconds(data.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60);
        var validFrom = minute.AddDays(-1); var validUntil = minute.AddDays(30); var details = D.Details;
        var input = new { suppliers = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken,
            checkerToken = issued[data.Checker.UserId].OpaqueToken, readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.Outsider.UserId].OpaqueToken,
            makerId = data.Maker.UserId.ToString(), checkerId = data.Checker.UserId.ToString(), readerId = data.Reader.UserId.ToString(), outsiderId = data.Outsider.UserId.ToString(),
            organizationId = "100", shopId = "101", warehouseId = "102", channelId = "103",
            policyId = D.PolicyId.ToString(), policyRevision = "1", trustedChannelReference = D.TrustedChannel, sourceReference = D.SourceReference,
            validFrom = validFrom.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), validUntil = validUntil.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            details = new { subjectReference = details.SubjectReference, legalName = details.LegalName, displayName = details.DisplayName,
                registrationNamespace = details.RegistrationNamespace, registrationValue = details.RegistrationValue, address = details.Address,
                contactName = details.ContactName, contactMethod = details.ContactMethod, contactValue = details.ContactValue },
            organizationCode = "TOKO", organizationName = "Synthetic cooperative", shopCode = "SHOP", shopName = "Synthetic fulfillment shop",
            warehouseCode = "WAREHOUSE", warehouseName = "Synthetic dispatch warehouse", channelCode = "RETAIL", channelName = "Synthetic retail channel",
            skuId = data.Procurement.Sku.Id.ToString(), skuRevision = data.Procurement.Sku.Version.ToString(), definitionId = data.Procurement.Sku.CurrentDefinitionId!.Value.ToString(),
            definitionVersion = data.Procurement.Sku.DefinitionVersion!.Value.ToString(), skuCode = "PROC-PENCIL-12", familyName = "Pensil pengadaan", packageLabel = "Dus", packageUnits = "12" };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "suppliers-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Supplier browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString());
            Assert.Contains("REAL_SUPPLIER_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        var proposal = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync(); var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync();
        var observation = await data.Rows(D.Observations).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, observation["actorId"].AsInt64);
        Assert.Equal("approve", decision["decision"].AsString); Assert.Equal(proposal["_id"], decision["proposalId"]);
        Assert.Equal(proposal["input"]["details"], observation["observed"]); Assert.Equal(D.TrustedChannel, observation["trustedChannelReference"].AsString);
        Assert.Equal(D.SourceReference, observation["sourceReference"].AsString); Assert.Equal(proposal["policy"]["_id"], observation["policyId"]);
        Assert.Equal(proposal["policy"]["revision"], observation["policyRevision"]);
        var mapping = Assert.Single(proposal["input"]["mappings"].AsBsonArray);
        Assert.Equal(data.Procurement.Sku.Id.Value, mapping["skuId"].AsInt64); Assert.Equal(data.Procurement.Sku.Version, mapping["expectedSkuRevision"].AsInt64);
        Assert.Equal(data.Procurement.Sku.CurrentDefinitionId!.Value.Value, mapping["definitionId"].AsInt64); Assert.Equal(data.Procurement.Sku.DefinitionVersion!.Value, mapping["definitionVersion"].AsInt64);
        Assert.Equal("000001", mapping["sourceAccountReference"].AsString); Assert.Equal("000012", mapping["externalItemCode"].AsString);
        Assert.Equal(validFrom.UtcDateTime, mapping["validFrom"].ToUniversalTime()); Assert.Equal(validUntil.UtcDateTime, mapping["validUntil"].ToUniversalTime());
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, proposal["_id"].AsInt64))["state"]);
        Assert.Equal(1, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + "propose")));
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + "observe")));
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Prefix + "approve")));
        }
        foreach (var element in original) {
            var rows = await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); Assert.Equal(element.Value, new BsonArray(rows));
        }
        await data.NoBusinessEffectsAsync();
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

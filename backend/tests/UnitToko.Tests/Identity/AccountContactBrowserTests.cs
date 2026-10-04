using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Identity;

/// <summary>Actual HTTPS/Next/API/Mongo with named synthetic issued sessions; no Auth0/provider acceptance claim.</summary>
[Collection("Mongo")]
public sealed class AccountContactBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_recovers_one_committed_contact_change_with_own_persistent_history_and_mobile_foreign_isolation()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UseSystemClock = true;
        var owner = await data.AddActorAsync(41, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 11), Permissions.OrderRead);
        var foreign = await data.AddActorAsync(42, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 12), Permissions.OrderRead);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        using var host = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production")
            .ConfigureTestServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); }));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); host.StartServer();
        var apiAddress = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        var profiles = data.Database.GetCollection<BsonDocument>("accountProfiles");
        foreach (var (actor, label) in new[] { (owner, "owner"), (foreign, "foreign") })
        {
            var subject = "auth0|synthetic-contact-browser-" + label; var now = data.Clock.UtcNow;
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await profiles.InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Akun kontak SINTETIS " + label }, { "verifiedEmail", label + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, label + "@example.invalid", true,
                configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
            var sessions = host.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issued = await new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-contact-browser-provider", ct));
            tokens[label] = issued.OpaqueToken;
        }
        var originalProfiles = new BsonArray(await profiles.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson();
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko private account contact rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("portal.unit-toko.co.id"); certificate.CertificateExtensions.Add(san.Build());
        using var leaf = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(15));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "account-contact-browser-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = new { apiAddress, origin = "https://portal.unit-toko.co.id", pfx = Convert.ToBase64String(leaf.Export(X509ContentType.Pfx, password)),
            password, spki = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), playwright, directory,
            tokens, ownerId = owner.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            foreignId = foreign.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-account-contact-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Contact browser did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        finally { output.WriteLine("Browser artifacts: " + directory); }
        var log = await stdout + await stderr; File.WriteAllText(Path.Combine(directory, "browser.log"), log); output.WriteLine(log);
        Assert.True(process.ExitCode == 0, log); Assert.Contains("REAL_ACCOUNT_CONTACT_BROWSER_PASS", log);
        Assert.Equal(originalProfiles, new BsonArray(await profiles.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson());
        var heads = data.Database.GetCollection<BsonDocument>(AccountContactRecords.Heads);
        var head = await heads.Find(new BsonDocument()).SingleAsync(); Assert.Equal(owner.UserId, head["_id"].AsInt64);
        Assert.Equal(1L, head["revision"].AsInt64); Assert.Equal("Kontak pilot Tebet", head["name"].AsString);
        Assert.Equal(1L, await data.Database.GetCollection<BsonDocument>(AccountContactRecords.History).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Database.GetCollection<BsonDocument>(AccountContactRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
        foreach (var table in new[] { "payments", "paymentAllocations", "journalEntries", "orders", "stockBalances" })
            Assert.False(await data.Database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) &&
                File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

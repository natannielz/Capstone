using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Organizations;
using Xunit.Abstractions;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;
using Data = UnitToko.Tests.Ordering.OrderCommercialPolicyTests.Data;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual production Next/HTTPS/Kestrel/Mongo and actual independently
/// approved commercial producers. Provider proofs, contacts and personnel are local synthetic inputs.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderCommercialPolicyBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_explicit_commercial_bundle_independent_approval_rejection_original_key_loss_recovery_and_two_versions_have_no_business_effects()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native browser rehearsal requires Windows");
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await new OrderCommercialPolicyTests(fixture).CreateAsync(); data.Existing.Clock.UseSystemClock = true;
        await data.Existing.Base.AddGrantAsync(data.Maker, 94001, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), [B.ApprovePermission]);
        var otherShop = await data.Existing.Organizations.Organizations.ProposeAsync(data.Existing.Organizations.ProfileMaker,
            new(OrganizationChangeKind.Create, ScopeNodeKind.Shop, 100, "COMMERCIAL-OTHER", "Synthetic foreign commercial seller"), Data.Meta("commercial-browser-other-shop"));
        var otherApproved = await data.Existing.Organizations.Organizations.ApproveAsync(data.Existing.Organizations.ProfileChecker, otherShop.Id, 1, Data.Meta("commercial-browser-other-shop-approved"));
        var samePerson = await data.Existing.Base.AddActorAsync(941, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), B.ReadPermission, B.ApprovePermission);
        var reader = await data.Existing.Base.AddActorAsync(942, new(GrantScopeKind.BuyingBranch, 10, 11), B.ReadPermission, Permissions.BranchPic);
        var foreignBuyer = await data.Existing.Base.AddActorAsync(943, new(GrantScopeKind.BuyingBranch, 10, 12), B.ReadPermission, Permissions.BranchPic);
        var foreignSeller = await data.Existing.Base.AddActorAsync(944, new(GrantScopeKind.FulfillmentShop, 100, ShopId: otherApproved.ScopeId!.Value, ChannelId: 103), B.ReadPermission, B.ProposePermission, B.ApprovePermission);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Existing.Clock, 941, 942, 943, 944);
        var actors = new Dictionary<string, ActorContext> { ["maker"] = data.Maker, ["checker"] = data.Checker, ["samePerson"] = samePerson,
            ["reader"] = reader, ["foreignBuyer"] = foreignBuyer, ["foreignSeller"] = foreignSeller };
        var clock = new BrowserClock();
        var service = new OrderCommercialPolicyService(data.Database, data.Existing.Unit, new(data.Database), clock, data.Existing.Organizations.Organizations);
        var sessions = new MongoIdentitySessionStore(data.Database, data.Existing.Unit, clock,
            new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        foreach (var actor in actors.Values) {
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = data.Existing.Clock.UtcNow;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic commercial participant " + actor.UserId }, { "verifiedEmail", "commercial-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        async Task<SessionIssue> Issue(string name) {
            var actor = actors[name]; var now = clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "commercial-" + actor.UserId + "@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            return await data.Existing.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "local-synthetic-commercial-provider", ct));
        }
        var issued = new Dictionary<string, string>(); foreach (var name in actors.Keys) issued[name] = (await Issue(name)).OpaqueToken;
        issued["expired"] = issued["checker"];
        var preserved = await BusinessSnapshot(data); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full";
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<OrderCommercialPolicyService>(); services.AddSingleton(service);
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, clock, controlKey, Issue));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local synthetic commercial rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var validFrom = DateTimeOffset.FromUnixTimeSeconds(data.Existing.Clock.UtcNow.AddMinutes(-2).ToUnixTimeSeconds() / 60 * 60); var validUntil = validFrom.AddDays(2);
        var prefix = "commercial-browser-" + Guid.NewGuid().ToString("N");
        var config = new { apiAddress, origin, controlKey, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), proofPrefix = prefix,
            clockFixture = "System UTC plus a monotonic six-minute offset for the genuine retained-factor freshness refusal; no clock rewind",
            tokens = issued, actorIds = actors.ToDictionary(pair => pair.Key, pair => pair.Value.UserId.ToString(CultureInfo.InvariantCulture)),
            seller = new { organizationId = "100", shopId = "101", channelId = "103", currency = "IDR" }, targetId = "11", targetKind = "branch",
            validFrom = validFrom.ToString("O"), validUntil = validUntil.ToString("O"),
            validFromLocal = validFrom.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            validUntilLocal = validUntil.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) };
        var directory = Path.Combine(root, "tmp", "production", "commercial-policy-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-commercial-policy-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Commercial browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("LOCAL_SYNTHETIC_COMMERCIAL_POLICY_BROWSER_PASS", lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log);
        }
        Assert.Equal(preserved, await BusinessSnapshot(data));
        Assert.Equal(3L, await data.Rows(B.Proposals).CountDocumentsAsync(new BsonDocument())); Assert.Equal(3L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        var current = await service.ReadAsync(data.Maker, data.Input().Target); Assert.Equal(2L, current["version"].AsInt64); Assert.True(current["current"].AsBoolean);
        var original = await data.Rows(B.Proposals).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).FirstAsync();
        Assert.Equal(data.Maker.UserId, original["actorId"].AsInt64);
        Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument { { "operation", B.Prefix + "propose" }, { "key", original["requestKey"] } }));
    }
    private sealed class BrowserClock : IClock
    {
        private long offset;
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref offset));
        internal void AdvanceFactorWindow() => Interlocked.CompareExchange(ref offset, (long)TimeSpan.FromMinutes(6).TotalMilliseconds, 0);
    }
    private sealed class FixtureControl(Data data, BrowserClock clock, string secret, Func<string, Task<SessionIssue>> issue) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/commercial")) { await invoke(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/commercial/".Length..];
                if (action == "state") {
                    await context.Response.WriteAsJsonAsync(new { proposals = await data.Rows(B.Proposals).CountDocumentsAsync(new BsonDocument()),
                        decisions = await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()), versions = await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()),
                        businessSha256 = Hash(await BusinessSnapshot(data)), durableSha256 = Hash(await data.StateAsync()) }); return;
                }
                if (action == "same-person") { await OrganizationAddressTests.MergePeopleAsync(data.Existing.Organizations, data.Maker.UserId, 941); context.Response.StatusCode = 204; return; }
                if (action == "advance-factor-window") { clock.AdvanceFactorWindow(); context.Response.StatusCode = 204; return; }
                if (action is "renew-maker" or "renew-checker") { var session = await issue(action["renew-".Length..]); await context.Response.WriteAsJsonAsync(new { token = session.OpaqueToken }); return; }
                context.Response.StatusCode = 404;
            }); next(app);
        };
    }
    private static async Task<BsonDocument> BusinessSnapshot(Data data)
    {
        var snapshot = new BsonDocument(); foreach (var name in new[] { "orders", "orderDrafts", "invoices", "stockBalances", "lots", "reservations", "movements", "journalEntries", "creditVersions", "cashMovements", "paymentAllocations" })
            snapshot[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return snapshot;
    }
    private static string Hash(BsonDocument value) => Convert.ToHexString(SHA256.HashData(value.ToBson())).ToLowerInvariant();
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the browser proof from the authoritative checkout");
    }
}

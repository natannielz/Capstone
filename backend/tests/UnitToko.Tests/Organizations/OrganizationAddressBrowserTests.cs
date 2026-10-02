using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Tests.Organizations;

/// <summary>Local opt-in actual Next/Kestrel/Mongo rehearsal. All identity and postal inputs are explicitly synthetic.</summary>
[Collection("Mongo")]
public sealed class OrganizationAddressBrowserTests(MongoFixture fixture, ITestOutputHelper output) {
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_proposes_independently_approves_exact_postal_address_recovers_committed_reply_and_limits_reader_scope() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The configured native browser fixture requires Windows");
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        // Mongo's real TTL monitor uses wall time, not the deterministic domain-test clock.
        data.Clock.UtcNow = DateTimeOffset.UtcNow;
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock,
            new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var actor in new[] { data.Maker, data.Checker, data.Reader, data.WarehouseReader }) {
            // Real synthetic producer protocol: actual persisted identity, protected session and
            // verified display profile. The production actor/session endpoints remain intact.
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "address-" + actor.UserId + "@example.invalid", true,
                "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
            issued[actor.UserId] = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-postal-provider", ct));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic postal participant " + actor.UserId }, { "verifiedEmail", "address-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        }
        var persistedSessions = await data.Rows("identitySessions").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(issued.Count, persistedSessions.Count);
        Assert.All(persistedSessions, session => Assert.True(session["absoluteExpiresAt"].ToUniversalTime() > DateTime.UtcNow));
        var original = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "journalEntries", "stockBalances", "invoices" }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); original[table] = new BsonArray(rows);
        }
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.RemoveAll<OrganizationMasterDataService>(); services.AddSingleton(data.Organizations);
            services.RemoveAll<OrganizationAddressService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko postal governance rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var postal = OrganizationAddressTestData.Postal;
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            organizationAddress = true, financeToken = issued[data.Maker.UserId].OpaqueToken, makerToken = issued[data.Maker.UserId].OpaqueToken,
            checkerToken = issued[data.Checker.UserId].OpaqueToken, readerToken = issued[data.Reader.UserId].OpaqueToken, outsiderToken = issued[data.WarehouseReader.UserId].OpaqueToken,
            makerId = data.Maker.UserId.ToString(), checkerId = data.Checker.UserId.ToString(), readerId = data.Reader.UserId.ToString(), outsiderId = data.WarehouseReader.UserId.ToString(),
            targetKind = "branch", targetId = OrganizationAddressTestData.Branch.Id.ToString(), targetCode = "TEBET", targetName = "Synthetic Tebet branch",
            address = new { line1 = postal.Line1, line2 = postal.Line2, city = postal.City, region = postal.Region, postalCode = postal.PostalCode, countryCode = postal.CountryCode } };
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "organization-address-" + Guid.NewGuid().ToString("N") + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Postal browser child did not start");
        async Task ReadAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) { lock (lines) { lines.AppendLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } } }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString());
            Assert.Contains("REAL_ORGANIZATION_ADDRESS_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        var proposal = await data.Rows(B.Proposals).Find(new BsonDocument()).SingleAsync(); var decision = await data.Rows(B.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, proposal["actorId"].AsInt64); Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64);
        Assert.NotEqual(proposal["actorId"], decision["actorId"]); Assert.Equal("approve", decision["decision"].AsString);
        Assert.StartsWith("oa.11.delivery.", proposal["requestKey"].AsString); Assert.StartsWith("oad." + proposal["_id"].AsInt64 + ".approve.", decision["requestKey"].AsString);
        var current = await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery);
        Assert.True(current["current"].AsBoolean); Assert.Equal(1L, current["version"].AsInt64);
        Assert.Equal(postal.Line1, current["address"]["line1"].AsString); Assert.Equal(postal.PostalCode, current["address"]["postalCode"].AsString);
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, "organization.address.propose")));
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, "organization.address.approve")));
        }
        // Advance only this local fixture's clock. The unchanged opaque session remains
        // live, while its real five-minute approval assurance has now aged out.
        data.Clock.UtcNow += TimeSpan.FromMinutes(6);
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new(apiAddress) }) {
            http.DefaultRequestHeaders.Host = new Uri(origin).Host;
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); http.DefaultRequestHeaders.Add("X-Forwarded-Host", new Uri(origin).Host);
            http.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
            http.DefaultRequestHeaders.Add("Cookie", "__Host-ut_session=" + issued[data.Checker.UserId].OpaqueToken);
            http.DefaultRequestHeaders.Add("X-Expected-Actor-Id", data.Checker.UserId.ToString());
            using var access = await http.GetAsync("/api/v1/access/current"); Assert.Equal(HttpStatusCode.OK, access.StatusCode);
            var view = await access.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(view.GetProperty("allowedActions").EnumerateArray(), action => action.GetString() == "organization.approve");
            using var csrfResponse = await http.GetAsync("/api/v1/identity/csrf"); Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
            var csrf = await csrfResponse.Content.ReadFromJsonAsync<JsonElement>();
            var cookies = csrfResponse.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';', 2)[0]).ToArray(); Assert.NotEmpty(cookies);
            http.DefaultRequestHeaders.Remove("Cookie"); http.DefaultRequestHeaders.Add("Cookie", "__Host-ut_session=" + issued[data.Checker.UserId].OpaqueToken + "; " + string.Join("; ", cookies));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/organizations/addresses/proposals/" + proposal["_id"].AsInt64 + "/preview-decision") { Content = JsonContent.Create(new { decision = "approve" }) };
            request.Headers.Add("Origin", origin); request.Headers.Add("X-CSRF-Token", csrf.GetProperty("requestToken").GetString()); request.Headers.Add("If-Match", "\"1\"");
            using var refused = await http.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("step_up_required", problem.GetProperty("code").GetString());
        }
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        foreach (var element in original) {
            var rows = await data.Rows(element.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence");
            Assert.Equal(element.Value, new BsonArray(rows));
        }
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

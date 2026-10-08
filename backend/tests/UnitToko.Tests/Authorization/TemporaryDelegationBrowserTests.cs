using System.Diagnostics;
using System.Globalization;
using System.Net;
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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class TemporaryDelegationBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_independently_approves_finite_delegation_and_recovers_all_original_operations_after_real_restart() {
        Assert.True(CollectionMigrations.CurrentVersion >= 113, "Compose the actual migration prefix before the delegation browser proof");
        var root = Root(); var frontend = Path.Combine(root, "frontend");
        if (Environment.GetEnvironmentVariable("UNITTOKO_DELEGATION_BROWSER_FRONTEND") is { Length: > 0 } candidateFrontend) {
            Assert.True(Path.IsPathFullyQualified(candidateFrontend), "The isolated frontend path must be absolute.");
            frontend = Path.GetFullPath(candidateFrontend);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar, frontend, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(frontend, "package.json")) && File.Exists(Path.Combine(frontend, "scripts", "verify-temporary-delegation-browser.mjs")),
                "The isolated frontend must contain the real application and delegation browser driver.");
        }
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var build = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(build));
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        var giver = await data.AddActorAsync(5100, new(GrantScopeKind.System), Permissions.AccessPropose);
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        await data.AddGrantAsync(giver, 5101, shop, [Permissions.StockRead], [Permissions.StockRead]);
        var recipient = await data.AddActorAsync(5200, shop, Permissions.CatalogRead);
        var checker = await data.AddActorAsync(5300, shop, Permissions.AccessApprove, Permissions.AccessRevoke);
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.StockRead }));
        var outsider = await data.AddActorAsync(5400, new(GrantScopeKind.BuyingBranch, 10, BranchId: 12), Permissions.AccessApprove);
        data.Clock.UseSystemClock = true;
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, giver.UserId, recipient.UserId, checker.UserId, outsider.UserId);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        using var host = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(new MongoUnitOfWork((MongoClient)data.Database.Client));
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
        }));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); host.StartServer();
        var apiAddress = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<long, string>();
        foreach (var actor in new[] { giver, recipient, checker, outsider }) {
            var subject = "auth0|synthetic-delegation-" + actor.UserId; var now = data.Clock.UtcNow;
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await data.Database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic delegation participant " + actor.UserId }, { "verifiedEmail", "delegation-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, "delegation-" + actor.UserId + "@example.invalid", true,
                configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
            var issued = await host.Services.GetRequiredService<MongoUnitOfWork>().ExecuteAsync((tx, ct) => host.Services.GetRequiredService<MongoIdentitySessionStore>()
                .CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-delegation-browser", ct));
            tokens[actor.UserId] = issued.OpaqueToken;
        }
        var protectedState = await BusinessAsync(data.Database);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local delegation rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "delegation-" + Guid.NewGuid().ToString("N");
        var nowWib = data.Clock.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var config = new {apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = (await File.ReadAllTextAsync(build)).Trim(), proofPrefix = prefix, temporaryDelegation = true,
            financeToken = tokens[giver.UserId], checkerToken = tokens[checker.UserId], deniedToken = tokens[outsider.UserId], deniedId = "5400",
            sourceGrantId = "5101", recipientId = "5200", recipientMembershipId = "5200",
            effectiveWib = nowWib.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), expiryWib = nowWib.AddHours(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)};
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Delegation browser child did not start");
        async Task Read(StreamReader reader) {while (await reader.ReadLineAsync() is { } line) lock (lines) {lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine);}}
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_TEMPORARY_DELEGATION_BROWSER_PASS", lines.ToString());
        } finally {if (!process.HasExited) {process.Kill(true); await process.WaitForExitAsync();} await Task.WhenAll(stdout, stderr); output.WriteLine("Actual child log: " + log);}
        var head = await data.Database.GetCollection<BsonDocument>(R.Heads).Find(new BsonDocument()).SingleAsync();
        var events = await data.Database.GetCollection<BsonDocument>(R.Events).Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync();
        Assert.Equal(new[] { "propose", "approve", "handover", "revoke" }, events.Select(row => row["action"].AsString).ToArray());
        Assert.Equal(new long[] { giver.UserId, checker.UserId, giver.UserId, giver.UserId }, events.Select(row => row["actorId"].AsInt64).ToArray());
        Assert.Equal(4L, head["revision"].AsInt64); Assert.Equal("revoked", head["status"].AsString); Assert.Equal(checker.UserId, head["approvedBy"].AsInt64);
        var grants = await data.Database.GetCollection<BsonDocument>("authorizationGrants").Find(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)).ToListAsync();
        var grant = Assert.Single(grants); Assert.Equal(AuthorizationBson.Grant(R.Grant(head)), new BsonDocument(grant.Elements.Where(field => field.Name != "fence")));
        Assert.Equal(4, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", R.Operation)));
        var unit = host.Services.GetRequiredService<MongoUnitOfWork>();
        await unit.ExecuteAsync(async (tx, ct) => {foreach (var row in events) Assert.Equal(row, await R.EventAsync(data.Database, tx, row["_id"].AsInt64, ct)); return true;});
        Assert.Equal(protectedState, await BusinessAsync(data.Database));
    }
    private static async Task<BsonDocument> BusinessAsync(IMongoDatabase database) {
        var result = new BsonDocument();
        foreach (var name in new[] { "orders", "orderDecisions", "stockBalances", "inventoryPools", "movements", "journalEntries", "payments", "cashMovements", "workItems" })
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static string Root() {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(folder.FullName, "backend", "UnitToko.slnx"))) return folder.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

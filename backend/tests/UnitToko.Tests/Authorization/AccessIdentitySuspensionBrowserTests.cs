using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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

namespace UnitToko.Tests.Authorization;

/// <summary>Local synthetic issued sessions, production Next/HTTPS, actual API/Mongo; no provider acceptance.</summary>
[Collection("Mongo")]
public sealed class AccessIdentitySuspensionBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_recovers_exact_account_suspension_once_and_denies_existing_and_new_business_sessions()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await StoreTestData.CreateAsync(fixture); data.Clock.UseSystemClock = true;
        var admin = await data.AddActorAsync(9007199254740993, new(GrantScopeKind.System),
            Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var target = await data.AddActorAsync(41, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.BranchOrderRequestRead);
        await data.AddGrantAsync(target, 41001, new(GrantScopeKind.BuyerOwned, 20, 21), [Permissions.BranchOrderRequestRead]);
        var scoped = await data.AddActorAsync(42001, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessRevoke);
        var outside = await data.AddActorAsync(42002, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.AccessRevoke);
        var otherAdmin = await data.AddActorAsync(42003, new(GrantScopeKind.System), Permissions.AccessRevoke);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var observer = new SuspensionObserver(data, target.UserId, configured, controlKey);
        using var host = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production")
            .ConfigureTestServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                services.AddSingleton<IStartupFilter>(observer); }));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); host.StartServer();
        var apiAddress = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        foreach (var (actor, label) in new[] { (admin, "admin"), (target, "target"), (scoped, "scoped"), (outside, "outside"), (otherAdmin, "otherAdmin") })
        {
            var subject = Subject(label); var now = data.Clock.UtcNow;
            await Rows(data, "identities").UpdateOneAsync(new BsonDocument { { "_id", new BsonInt64(actor.UserId) } },
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await Rows(data, "accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Akun R11 SINTETIS " + label }, { "verifiedEmail", label + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var sessions = host.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issued = await new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), Proof(configured, label, now),
                "synthetic-r11-browser-provider", ct));
            tokens[label] = issued.OpaqueToken;
        }
        var preserved = await PreservedAsync(data);
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko private synthetic R11 rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("portal.unit-toko.co.id"); certificate.CertificateExtensions.Add(san.Build());
        using var leaf = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(15));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "identity-suspension-browser-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = new { apiAddress, origin = "https://portal.unit-toko.co.id", pfx = Convert.ToBase64String(leaf.Export(X509ContentType.Pfx, password)),
            password, spki = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), playwright, directory, tokens, controlKey,
            adminId = Id(admin.UserId), targetId = Id(target.UserId), scopedId = Id(scoped.UserId), outsideId = Id(outside.UserId), otherAdminId = Id(otherAdmin.UserId) };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-identity-suspension-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Identity suspension browser did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        finally { output.WriteLine("Synthetic browser artifacts: " + directory); }
        var log = await stdout + await stderr; await File.WriteAllTextAsync(Path.Combine(directory, "browser.log"), log); output.WriteLine(log);
        Assert.True(process.ExitCode == 0, log); Assert.Contains("REAL_IDENTITY_SUSPENSION_BROWSER_PASS", log);
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "proof.json")));
        var saved = Assert.Single(observer.Commits);
        var businessRead = Assert.Single(observer.BusinessReads);
        foreach (var name in businessRead.Before.Names)
        {
            var beforeRows = businessRead.Before[name].AsBsonArray; var afterRows = businessRead.After[name].AsBsonArray;
            Assert.Equal(2, beforeRows.Count); Assert.Equal(beforeRows.Count, afterRows.Count);
            for (var index = 0; index < beforeRows.Count; index++)
            {
                var expected = beforeRows[index].DeepClone().AsBsonDocument;
                expected["fence"] = checked(ReadFence(expected) + 1);
                Assert.Equal(expected, afterRows[index].AsBsonDocument);
            }
        }
        Assert.Equal(businessRead.After, saved.ScopedBefore);
        Assert.Equal(saved.ScopedBefore, saved.ScopedAfter); // the suspension preserves exact target grants, including managed read fences
        Assert.Equal("active", saved.Before["status"].AsString); Assert.Equal(1L, saved.Before["grantVersion"].AsInt64);
        Assert.Equal("suspended", saved.After["status"].AsString); Assert.Equal(2L, saved.After["grantVersion"].AsInt64);
        Assert.Equal(saved.Before["fence"].AsInt64 + 1, saved.After["fence"].AsInt64);
        Assert.Equal(saved.Before["securityVersion"], saved.After["securityVersion"]);
        Assert.Equal(saved.Key, proof.RootElement.GetProperty("key").GetString());
        Assert.Equal(1, observer.NewSessionDenials);
        var identity = await Rows(data, "identities").Find(new BsonDocument("_id", target.UserId)).SingleAsync();
        Assert.Equal(saved.After, identity); // denied reads/admission cannot further fence an inactive identity
        var history = await Rows(data, "authorizationHistory").Find(new BsonDocument()).SingleAsync();
        Assert.Equal("access.identity.suspended", history["operation"].AsString); Assert.Equal(admin.UserId, history["actorId"].AsInt64);
        Assert.Equal(target.UserId, history["subjectId"].AsInt64); Assert.Equal("system", history["scope"]["kind"].AsString);
        Assert.Equal(1L, history["before"]["grantVersion"].AsInt64); Assert.Equal(2L, history["after"]["grantVersion"].AsInt64);
        Assert.Equal(proof.RootElement.GetProperty("reason").GetString(), history["reason"].AsString);
        Assert.Equal(Id(history["_id"].AsInt64), proof.RootElement.GetProperty("historyId").GetString());
        var receipt = await Rows(data, "idempotencyRecords").Find(new BsonDocument()).SingleAsync();
        Assert.Equal("access.identity.suspend", receipt["operation"].AsString); Assert.Equal(admin.UserId, receipt["actorId"].AsInt64);
        Assert.Equal(saved.Key, receipt["key"].AsString); Assert.Equal(target.UserId, receipt["result"]["subjectId"].AsInt64);
        Assert.Equal(2L, receipt["result"]["grantVersion"].AsInt64); Assert.Equal("suspended", receipt["result"]["status"].AsString);
        Assert.NotEmpty(await Rows(data, "authorizationReadEvents").Find(new BsonDocument { { "actorId", new BsonInt64(admin.UserId) } }).ToListAsync());
        var after = await PreservedAsync(data);
        foreach (var name in preserved.Names)
        {
            var expected = preserved[name].DeepClone();
            if (name is "memberships" or "authorizationGrants")
            {
                // Only the exact target rows proven above may have the one legitimate business-read fence increment.
                // All actor/target status, versions, permissions, scope, lifetime and every other field remain exact.
                var rows = expected.AsBsonArray;
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index].AsBsonDocument;
                    if (row["userId"].AsInt64 != target.UserId) continue;
                    var originalRead = Assert.Single(businessRead.Before[name].AsBsonArray.Select(value => value.AsBsonDocument),
                        value => value["_id"] == row["_id"]);
                    Assert.Equal(row, originalRead);
                    rows[index] = Assert.Single(businessRead.After[name].AsBsonArray.Select(value => value.AsBsonDocument),
                        value => value["_id"] == row["_id"]).DeepClone();
                }
            }
            Assert.True(expected.Equals(after[name]), name + " changed beyond the exact proven business-read fence");
        }
        Assert.Null(await host.Services.GetRequiredService<MongoIdentitySessionStore>().ReadAsync(tokens["target"]));
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string Subject(string label) => "auth0|synthetic-r11-browser-" + label;
    private static ProviderIdentityProof Proof(Auth0Options configured, string label, DateTimeOffset now) => new(configured.Issuer,
        Subject(label), label + "@example.invalid", true, configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static long ReadFence(BsonDocument row)
    {
        if (!row.TryGetValue("fence", out var value)) return 0;
        Assert.True(value.IsInt64); Assert.True(value.AsInt64 >= 0); return value.AsInt64;
    }
    private static async Task<BsonDocument> TargetScopedAsync(StoreTestData data, long userId)
    {
        var rows = new BsonDocument();
        foreach (var name in new[] { "memberships", "authorizationGrants" })
            rows[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument("userId", userId)).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return rows;
    }
    private static async Task<BsonDocument> PreservedAsync(StoreTestData data)
    {
        var rows = new BsonDocument();
        foreach (var name in new[] { "memberships", "authorizationGrants", "accountProfiles", "auditEvents", "payments", "paymentSubmissions",
            "paymentAllocations", "bankReceipts", "journalEntries", "orders", "reservations", "stockBalances", "movements" })
            rows[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return rows;
    }
    private sealed record Commit(string Key, BsonDocument Before, BsonDocument After, BsonDocument ScopedBefore, BsonDocument ScopedAfter);
    private sealed record BusinessRead(BsonDocument Before, BsonDocument After);
    // Keyed loopback-only test control: performs real session admission, never installs a product route or provider response.
    private sealed class SuspensionObserver(StoreTestData data, long targetId, Auth0Options options, string key) : IStartupFilter
    {
        internal ConcurrentQueue<Commit> Commits { get; } = new();
        internal ConcurrentQueue<BusinessRead> BusinessReads { get; } = new();
        internal int NewSessionDenials { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path == "/__fixture/r11/new-session")
                {
                    if (context.Request.Method != "POST" || context.Connection.RemoteIpAddress is not { } address ||
                        !IPAddress.IsLoopback(address) || context.Request.Headers["X-Fixture-Control"].ToString() != key)
                    { context.Response.StatusCode = 403; return; }
                    var current = await Rows(data, "identities").Find(new BsonDocument("_id", targetId)).SingleAsync();
                    var sessions = context.RequestServices.GetRequiredService<MongoIdentitySessionStore>();
                    try
                    {
                        await context.RequestServices.GetRequiredService<MongoUnitOfWork>().ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                            new(targetId, current["securityVersion"].AsInt64, current["grantVersion"].AsInt64, options.Issuer, Subject("target")),
                            Proof(options, "target", data.Clock.UtcNow), "synthetic-r11-browser-provider", ct));
                        throw new InvalidOperationException("Inactive target was admitted to a new real session");
                    }
                    catch (AuthorizationDeniedException denied) when (denied.Code == "session_unavailable")
                    { NewSessionDenials++; context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { code = denied.Code }); }
                    return;
                }
                var observed = context.Request.Method == "POST" && context.Request.Path == "/api/v1/access/subjects/" + Id(targetId) + "/suspend";
                var businessRead = context.Request.Method == "POST" && context.Request.Path == "/api/v1/ordering/requests/query";
                var before = observed ? await Rows(data, "identities").Find(new BsonDocument("_id", targetId)).SingleAsync() : null;
                var scopedBefore = observed || businessRead ? await TargetScopedAsync(data, targetId) : null;
                await following(context);
                if (observed && context.Response.StatusCode == 200)
                    Commits.Enqueue(new(context.Request.Headers["Idempotency-Key"].ToString(), before!.DeepClone().AsBsonDocument,
                        (await Rows(data, "identities").Find(new BsonDocument("_id", targetId)).SingleAsync()).DeepClone().AsBsonDocument,
                        scopedBefore!.DeepClone().AsBsonDocument, await TargetScopedAsync(data, targetId)));
                if (businessRead && context.Response.StatusCode == 200)
                    BusinessReads.Enqueue(new(scopedBefore!.DeepClone().AsBsonDocument, await TargetScopedAsync(data, targetId)));
            });
            next(app);
        };
    }
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) &&
                File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

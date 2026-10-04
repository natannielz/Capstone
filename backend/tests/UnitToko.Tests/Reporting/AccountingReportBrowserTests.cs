using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Reporting;

/// <summary>Actual HTTPS/production Next/.NET/Mongo with named synthetic sessions, books and posted journals.</summary>
[Collection("Mongo")]
public sealed class AccountingReportBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Browser_reads_scoped_journals_downloads_whole_snapshot_and_preserves_mobile_foreign_isolation()
    {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        await using var data = await AccountingReportTestData.CreateAsync(fixture);
        var endDate = data.EndDate; var journalIds = new List<string>();
        for (var index = 0; index < 21; index++) {
            var journal = await data.PostAsync(1000 + index, date: endDate, description: "Jurnal SINTETIS untuk laporan " + (index + 1));
            journalIds.Add(journal.Id.ToString(CultureInfo.InvariantCulture));
        }
        var originalFinancial = await data.FinancialStateAsync();
        var book = await data.Rows("accountingBooks").Find(new BsonDocument("canonicalKey", AccountingReportTestData.Key.CanonicalKey)).SingleAsync();
        data.Clock.UseSystemClock = true;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var configured = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        using var host = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production")
            .ConfigureTestServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); }));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); host.StartServer();
        var apiAddress = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var tokens = new Dictionary<string, string>();
        foreach (var (actor, label) in new[] { (data.Reader, "owner"), (data.ForeignReader, "foreign") }) {
            var subject = "auth0|synthetic-accounting-report-browser-" + label; var now = data.Clock.UtcNow;
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
                Builders<BsonDocument>.Update.Set("issuer", configured.Issuer).Set("subject", subject).Set("securityVersion", 1L));
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Akun laporan SINTETIS " + label }, { "verifiedEmail", label + "@example.invalid" },
                { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
            var proof = new ProviderIdentityProof(configured.Issuer, subject, label + "@example.invalid", true,
                configured.ConnectionId, now, now, "otp", configured.AssurancePolicyVersion);
            var sessions = host.Services.GetRequiredService<MongoIdentitySessionStore>();
            var issued = await new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                new(actor.UserId, 1, actor.GrantVersion, configured.Issuer, subject), proof, "synthetic-report-browser-provider", ct));
            tokens[label] = issued.OpaqueToken;
        }
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=UnitToko private scoped reporting rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("portal.unit-toko.co.id"); certificate.CertificateExtensions.Add(san.Build());
        using var leaf = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(15));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "accounting-report-browser-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = new { apiAddress, origin = "https://portal.unit-toko.co.id", pfx = Convert.ToBase64String(leaf.Export(X509ContentType.Pfx, password)),
            password, spki = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), playwright, directory, tokens,
            ownerId = data.Reader.UserId.ToString(CultureInfo.InvariantCulture), foreignId = data.ForeignReader.UserId.ToString(CultureInfo.InvariantCulture),
            bookId = book["_id"].AsInt64.ToString(CultureInfo.InvariantCulture), endDate, total = "2100.00", journalIds,
            foreignKey = new { organizationId = "100",
                shopId = data.Setup.ForeignShopId.ToString(CultureInfo.InvariantCulture), channelId = "103", currency = "IDR" } };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-accounting-report-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Reporting browser did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        finally { output.WriteLine("Browser artifacts: " + directory); }
        var log = await stdout + await stderr; File.WriteAllText(Path.Combine(directory, "browser.log"), log); output.WriteLine(log);
        Assert.True(process.ExitCode == 0, log); Assert.Contains("REAL_ACCOUNTING_REPORT_BROWSER_PASS", log);
        Assert.Equal(originalFinancial.ToJson(), (await data.FinancialStateAsync()).ToJson());
        var audits = await data.Rows("accountingReportReadEvents").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(1, audits.Count(row => row["operation"] == "accounting.report.export"));
        Assert.Equal(3, audits.Count(row => row["operation"] == "accounting.report.read"));
        Assert.All(audits.Where(row => row["operation"] == "accounting.report.read"), row => {
            Assert.Equal(data.Reader.UserId, row["actorId"].AsInt64); Assert.Equal(21, row["journalCount"].AsInt32);
            Assert.Equal(42, row["lineCount"].AsInt32); Assert.Equal(book["_id"], row["bookId"]);
        });
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "backend", "tests"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative repository root was not found");
    }
}

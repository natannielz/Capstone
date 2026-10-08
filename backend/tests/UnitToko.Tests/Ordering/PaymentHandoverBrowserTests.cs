using System.Diagnostics;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using Xunit.Abstractions;
using H = UnitToko.Platform.Billing.PaymentReviewHandoverRecords;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentHandoverBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsPaymentBrowserFact]
    public async Task Browser_claims_real_delegated_review_after_revocation_and_recovers_original_key_after_actual_restart() {
        var root = Root(); var frontend = Path.Combine(root, "frontend");
        if (Environment.GetEnvironmentVariable("UNITTOKO_PAYMENT_HANDOVER_BROWSER_FRONTEND") is {Length: > 0} path) {
            Assert.True(Path.IsPathFullyQualified(path)); frontend = Path.GetFullPath(path);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "tmp")) + Path.DirectorySeparatorChar, frontend, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(File.Exists(Path.Combine(frontend, "package.json")) && File.Exists(Path.Combine(frontend, "scripts", "verify-payment-handover-browser.mjs")));
        var build = Path.Combine(frontend, ".next-full", "BUILD_ID"); Assert.True(File.Exists(build));
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture); await d.RevokeAsync(); var data = d.Data;
        foreach (var actor in new[] {d.Claimant, data.Reader, d.Outsider}) await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
            {"_id", actor.UserId}, {"schemaVersion", 1}, {"version", 1L}, {"displayName", "Synthetic handover reviewer " + actor.UserId},
            {"verifiedEmail", "handover-" + actor.UserId + "@example.invalid"}, {"verifiedAt", data.Read.Catalog.Clock.UtcNow.UtcDateTime}, {"updatedAt", data.Read.Catalog.Clock.UtcNow.UtcDateTime}});
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Read.Database.DatabaseNamespace.DatabaseName; settings["Release:Profile"] = "full";
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var host = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton(data.Read.Catalog.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Read.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(new MongoUnitOfWork(data.Read.Catalog.Client));
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Read.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Read.Catalog.Sessions);
            // The same real payment/evidence owners as the existing payment browser fixture, with actual approved private image storage.
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Read.Payment.Invoice.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Read.Payment.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(data.Read.Service);
            services.RemoveAll<BankReceiptService>(); services.AddSingleton(data.Bank.Service);
            services.RemoveAll<PaymentVerificationService>(); services.AddSingleton(data.Service);
        }));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); host.StartServer();
        var apiAddress = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var before = await d.BusinessAsync(); const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local payment handover rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var prefix = "payment-handover-" + Guid.NewGuid().ToString("N");
        var config = new {apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            buildId = (await File.ReadAllTextAsync(build)).Trim(), proofPrefix = prefix, paymentHandover = true,
            financeToken = data.Read.Catalog.Issued[d.Claimant.UserId].OpaqueToken, readerToken = data.Read.Catalog.Issued[data.Reader.UserId].OpaqueToken,
            deniedToken = data.Read.Catalog.Issued[d.Outsider.UserId].OpaqueToken, claimantId = d.Claimant.UserId.ToString(),
            originalAssigneeId = d.Recipient.UserId.ToString(), declarationId = d.Id.ToString(), delegationId = d.DelegationId.ToString()};
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) {WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true};
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-invoice-browser.mjs")); start.Environment["NODE_ENV"] = "production";
        start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Payment handover browser did not start");
        async Task Read(StreamReader reader) {while (await reader.ReadLineAsync() is { } line) lock (lines) {lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine);}}
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_PAYMENT_HANDOVER_BROWSER_PASS", lines.ToString());
        } finally {if (!process.HasExited) {process.Kill(true); await process.WaitForExitAsync();} await Task.WhenAll(stdout, stderr); output.WriteLine("Child evidence: " + log);}
        var proof = await data.Rows(H.Collection).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(d.DelegationId, proof["delegationId"].AsInt64); Assert.Equal(3L, proof["delegationRevision"].AsInt64);
        Assert.Equal(d.Recipient.UserId, proof["originalAssigneeId"].AsInt64); Assert.Equal(d.Claimant.UserId, proof["actorId"].AsInt64);
        Assert.Equal("revoked", proof["delegationState"].AsString); Assert.Equal(2L, proof["reviewRevision"].AsInt64);
        Assert.Equal(2, await data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument {{"operation", W.Operation}, {"actorId", d.Claimant.UserId}}));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument {{"operation", W.Operation}, {"actorId", d.Claimant.UserId}}));
        var recovered = await data.Service.RecoverHandoverAsync(d.Claimant, d.Id, proof["requestKey"].AsString); Assert.NotNull(recovered); Assert.Equal(proof["_id"], recovered["actionId"]);
        Assert.Equal(before, await d.BusinessAsync());
    }
    private static string Root() {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(folder.FullName, "backend", "UnitToko.slnx"))) return folder.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

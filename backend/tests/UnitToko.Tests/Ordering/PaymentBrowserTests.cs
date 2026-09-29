using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsPaymentBrowserFact]
    public async Task Browser_recovers_draft_reservation_and_original_upload_without_duplicate_financial_effects() {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE"); var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright)); Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var files = await EvidenceAdmissionTestData.CreateAsync(fixture); await using var data = await PaymentDraftData.CreateAsync(fixture); var order = data.Invoice.Order;
        var policy = EvidenceAdmissionSchemaTests.Policy(); policy["_id"] = (await new IdAllocator(order.Database).NextAsync("evidenceStorePolicies")).Value; await data.Invoice.Rows("evidenceStorePolicies").InsertOneAsync(policy);
        var evidence = new PaymentEvidenceService(order.Database, new(order.Prices.Catalog.Client), new(order.Database), order.Prices.Catalog.Clock, data.Service, files.Registration);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(data.Invoice.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentEvidenceService>(); services.AddSingleton(evidence);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko payment rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            buyerToken = order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken, outsiderToken = order.Prices.Catalog.Issued[data.Invoice.Finance.UserId].OpaqueToken,
            buyerId = order.Buyer.UserId.ToString(), transferDate = data.Input.TransferDate.ToString("yyyy-MM-dd") };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-payment-browser.mjs")); start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_PAYMENT_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, await data.Invoice.Count(PaymentDraftBson.Heads)); Assert.Equal(2, await data.Invoice.Count(PaymentDraftBson.Versions));
        Assert.Equal(1, await data.Invoice.Count("documentVersions")); Assert.Equal(0, await data.Invoice.Count("payments")); Assert.Equal(0, await data.Invoice.Count("bankReceipts")); Assert.Equal(1, await data.Invoice.Count("journalEntries"));
        var head = await data.Invoice.Rows(PaymentDraftBson.Heads).Find(new BsonDocument()).SingleAsync(); Assert.Equal("draft", head["state"]);
        var version = await data.Invoice.Rows("documentVersions").Find(new BsonDocument()).SingleAsync(); Assert.Equal("quarantined", version["state"]); Assert.Equal("unavailable", version["scanStatus"]);
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
public sealed class WindowsPaymentBrowserFactAttribute : FactAttribute
{
    public WindowsPaymentBrowserFactAttribute() {
        if (!OperatingSystem.IsWindows()) Skip = "The real private-file adapter requires Windows acceptance; other hosts remain unavailable.";
        else if (Environment.GetEnvironmentVariable("UNITTOKO_STOCK_BROWSER") != "1") Skip = "Opt-in real HTTPS/Edge/Next/Mongo/private-file rehearsal.";
    }
}

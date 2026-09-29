using System.Diagnostics;
using System.Net;
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
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class ShipmentDispatchBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_confirms_physical_lot_and_courier_and_recovers_one_committed_dispatch_without_resending() {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next", "BUILD_ID")));
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture); var order = data.Order;
        await order.Prices.Catalog.Base.AddGrantAsync(order.Buyer, 6510, CatalogAuthoringTestData.Audience, [UnitToko.Modules.Identity.Permissions.ShipmentRead]);
        await order.Prices.Catalog.Base.AddGrantAsync(data.Picker, 6511, CatalogAuthoringTestData.Shop, [UnitToko.Modules.Identity.Permissions.ShipmentRead]);
        foreach (var actor in new[] { data.Dispatcher, data.Picker, order.Buyer })
            await order.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Staf fixture lokal" }, { "verifiedEmail", "dispatch-" + actor.UserId + "@example.invalid" }, { "verifiedAt", order.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", order.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = order.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(order.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(order.Prices.Catalog.Sessions);
            services.RemoveAll<ShipmentDispatchService>(); services.AddSingleton(data.Service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko dispatch rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var input = new { apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password, playwrightPackage = playwright,
            dispatcherToken = order.Prices.Catalog.Issued[data.Dispatcher.UserId].OpaqueToken, deniedToken = order.Prices.Catalog.Issued[data.Picker.UserId].OpaqueToken,
            courierToken = order.Prices.Catalog.Issued[data.Courier.UserId].OpaqueToken, buyerToken = order.Prices.Catalog.Issued[order.Buyer.UserId].OpaqueToken,
            dispatcherId = data.Dispatcher.UserId.ToString(), deniedId = data.Picker.UserId.ToString(), orderId = data.Source.OrderId.ToString(), suborderId = data.Source.SuborderId.ToString(), courierId = data.Courier.UserId.ToString() };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-shipment-dispatch-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser child did not start");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("REAL_SHIPMENT_DISPATCH_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var shipment = await order.Rows("shipments").Find(new BsonDocument()).SingleAsync(); Assert.Equal(data.Courier.UserId, shipment["courierId"].AsInt64);
        Assert.Equal(1, shipment["pickedLots"][0]["packages"].AsInt32); Assert.Equal(1L, shipment["number"].AsInt64);
        var stock = await order.Rows("stockBalances").Find(new BsonDocument()).SingleAsync(); Assert.Equal(9L, stock["onHand"].AsInt64); Assert.Equal(1L, stock["reserved"].AsInt64);
        Assert.Equal(2, await order.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDispatchBson.Operation)));
        Assert.Equal(1, await order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.shipment.dispatched")));
        Assert.Equal(1, await order.Rows("issuedDocuments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await order.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDocumentService.Operation)));
        Assert.Equal(1, await order.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "fulfillment.surat-jalan.issued")));
        Assert.Equal(1L, (await order.Service.ReadOwnAsync(order.Buyer, data.Source.OrderId.Value)).Progress.DispatchSequence);
        Assert.Equal(0, await order.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

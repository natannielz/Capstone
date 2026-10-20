using System.Diagnostics;
using System.Globalization;
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
using UnitToko.Platform.Documents;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Fulfillment;

// Source-only candidate until the real contiguous 1..120 migrations and browser build are composed.
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentCourierBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_handover_recovers_original_key_after_restart_and_current_courier_receives_without_rewriting_departure()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Root(); var frontend = Frontend(root);
        var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        await using var data = await ShipmentCourierTestData.CreateAsync(fixture, systemClock: true);
        await data.RefreshActorsAsync();
        var shipment = await data.Queries.ReadAsync(data.CourierA, data.ShipmentId);
        var line = Assert.Single(shipment.Lines);
        var document = await data.Documents.DownloadAsync(data.CourierA, data.ShipmentId, data.DocumentId);
        var preview = await data.Service.PreviewAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId);
        Assert.Equal(data.CourierA.UserId, shipment.CourierId.Value);
        Assert.Equal(1, preview.Task.CurrentAssignment.Revision);
        Assert.Empty(preview.Task.History);
        var originalShipment = await data.Rows("shipments").Find(new BsonDocument("_id", data.ShipmentId)).SingleAsync();
        var originalDocument = await data.Rows(IssuedDocumentStore.Collection).Find(new BsonDocument("_id", data.DocumentId)).SingleAsync();
        var before = await data.BusinessAsync();
        Assert.Equal(0, await data.Rows(ShipmentReceiptRecords.Receipts).CountDocumentsAsync(new BsonDocument()));

        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Data.Catalog.Sessions);
        }));
        // Every shipment, assignment, challenge and receipt owner comes from actual production DI.
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=Local courier assignment rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var evidence = Path.Combine(root, "tmp", "production", "courier-assignment-browser-results"); Directory.CreateDirectory(evidence);
        var prefix = "courier-assignment-" + Guid.NewGuid().ToString("N");
        var handoverCheckedPath = Path.Combine(evidence, prefix + "-handover-checked.txt");
        await data.RefreshActorsAsync();
        var input = new {
            courierAssignment = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = File.ReadAllText(Path.Combine(frontend, ".next-full", "BUILD_ID")).Trim(),
            evidenceDirectory = evidence, proofPrefix = prefix, handoverCheckedPath,
            adminToken = data.Data.Catalog.Issued[data.Admin.UserId].OpaqueToken, adminId = Text(data.Admin.UserId),
            courierAToken = data.Data.Catalog.Issued[data.CourierA.UserId].OpaqueToken, courierAId = Text(data.CourierA.UserId), courierAName = shipment.CourierName,
            courierBToken = data.Data.Catalog.Issued[data.CourierB.UserId].OpaqueToken, courierBId = Text(data.CourierB.UserId), courierBName = preview.CourierName,
            recipientToken = data.Data.Catalog.Issued[data.Recipient.UserId].OpaqueToken, recipientId = Text(data.Recipient.UserId),
            wrongScopeToken = data.Data.Catalog.Issued[data.WrongScope.UserId].OpaqueToken, wrongScopeId = Text(data.WrongScope.UserId),
            shipmentId = Text(data.ShipmentId), documentId = Text(data.DocumentId),
            shipmentReference = FormattableString.Invariant($"{shipment.NumberEntity}/{shipment.NumberChannel}/{shipment.NumberYear}/{shipment.Number}"),
            documentHash = document.Document.ContentHash, documentLength = document.Document.ByteLength, packages = line.Packages,
            oldChallengeId = data.Challenge.Id.ToString(), oldChallengeToken = data.Challenge.Token
        };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = frontend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(frontend, "scripts", "verify-courier-assignment-harness.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Courier assignment browser did not start");
        var captured = new StringBuilder(); var handoverChecked = false;
        async Task ReadAsync(StreamReader reader) {
            while (await reader.ReadLineAsync() is { } message) {
                lock (captured) captured.AppendLine(message);
                if (message == "VALIDATE_COURIER_HANDOVER_READY") {
                    Assert.False(handoverChecked); handoverChecked = true;
                    Assert.Equal(before, await data.BusinessAsync());
                    Assert.Equal(1, await data.Rows(ShipmentCourierRecords.Events).CountDocumentsAsync(new BsonDocument()));
                    Assert.Equal(0, await data.Rows(ShipmentReceiptRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
                    Assert.Equal(originalShipment, await data.Rows("shipments").Find(new BsonDocument("_id", data.ShipmentId)).SingleAsync());
                    Assert.Equal(originalDocument, await data.Rows(IssuedDocumentStore.Collection).Find(new BsonDocument("_id", data.DocumentId)).SingleAsync());
                    await File.WriteAllTextAsync(handoverCheckedPath, "Actual handover and original-key recovery preserve the original stock/order/money/shipment/document snapshot before delivery receipt");
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            var exit = process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6));
            var first = await Task.WhenAny(exit, stdout, stderr); if (first.IsFaulted) await first;
            await exit; await Task.WhenAll(stdout, stderr); output.WriteLine(captured.ToString());
            Assert.True(process.ExitCode == 0, captured.ToString()); Assert.Contains("REAL_COURIER_ASSIGNMENT_BROWSER_PASS", captured.ToString()); Assert.True(handoverChecked);
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }

        await data.RefreshActorsAsync();
        Assert.Equal(originalShipment, await data.Rows("shipments").Find(new BsonDocument("_id", data.ShipmentId)).SingleAsync());
        Assert.Equal(originalDocument, await data.Rows(IssuedDocumentStore.Collection).Find(new BsonDocument("_id", data.DocumentId)).SingleAsync());
        Assert.Equal(document, await data.Documents.DownloadAsync(data.CourierB, data.ShipmentId, data.DocumentId));
        var change = await data.Rows(ShipmentCourierRecords.Events).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Admin.UserId, change["actorId"].AsInt64); Assert.Equal(data.CourierA.UserId, change["previousCourierId"].AsInt64);
        Assert.Equal(data.CourierB.UserId, change["courierId"].AsInt64); Assert.Equal(2L, change["revision"].AsInt64);
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentCourierRecords.Operation)));
        Assert.Equal(1, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("operation", ShipmentCourierRecords.Operation)));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", ShipmentCourierRecords.Operation)));
        var current = await data.Service.ReadAsync(data.Admin, data.ShipmentId);
        Assert.True(current.Received); Assert.False(current.CanHandover); Assert.Single(current.History);
        Assert.Equal(ShipmentCourierRecords.View(change), await data.Service.RecoverAsync(data.Admin, change["requestKey"].AsString));
        var receipt = await data.Rows(ShipmentReceiptRecords.Receipts).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Recipient.UserId, receipt["recipientId"].AsInt64); Assert.Equal(data.CourierB.UserId, receipt["courierId"].AsInt64);
        Assert.Equal(2, await data.Rows(ShipmentReceiptRecords.Challenges).CountDocumentsAsync(new BsonDocument()));
        var oldBinding = await data.Rows(ShipmentCourierRecords.Bindings).Find(new BsonDocument("_id", "challenge:" + data.Challenge.Id)).SingleAsync();
        var receiptBinding = await data.Rows(ShipmentCourierRecords.Bindings).Find(new BsonDocument("_id", "receipt:" + receipt["_id"].AsInt64)).SingleAsync();
        Assert.Equal(1L, oldBinding["assignmentRevision"].AsInt64); Assert.Equal(2L, receiptBinding["assignmentRevision"].AsInt64);
        Assert.Equal(0, await data.Rows(DeliveryAttemptRecords.Attempts).CountDocumentsAsync(new BsonDocument()));
    }
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Frontend(string root) {
        var path = Environment.GetEnvironmentVariable("UNITTOKO_COURIER_BROWSER_FRONTEND") ?? Path.Combine(root, "frontend");
        Assert.True(Path.IsPathFullyQualified(path)); path = Path.GetFullPath(path);
        Assert.True(path == Path.Combine(root, "frontend") || path.StartsWith(Path.Combine(root, "tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(path, ".next-full", "BUILD_ID"))); return path;
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}

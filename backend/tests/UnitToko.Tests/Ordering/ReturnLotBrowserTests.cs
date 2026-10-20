using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
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
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;

// Synthetic local accounts/policies and operational admission, with genuine
// setup, dispatch, recipient, invoice, return and inspection producers. No
// provider, activation, physical release, refund or payment acceptance claimed.
[Collection("Mongo")]
public sealed class ReturnLotBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_records_only_manual_whole_package_lot_finding_recovers_original_reply_loss_and_preserves_quarantine_stock_and_money() {
        var root = Root(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node));
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        Assert.True(File.Exists(Path.Combine(root, "frontend", ".next-full", "BUILD_ID")));
        Assert.True(CollectionMigrations.CurrentVersion >= 95, "The M95 return-lot schema must be installed");
        await using var data = await WholePackageAsync();
        var invoice = data.Invoice; var catalog = invoice.Order.Prices.Catalog; var clock = catalog.Clock;
        var units = Assert.Single(data.Issued.Detail.Lines).BaseUnitsPerPackage; Assert.True(units > 1);
        var (service, caseId, line) = await ReturnQuarantineTests.Setup(data, units); var actor = invoice.Finance;
        await catalog.Base.AddGrantAsync(actor, 99832, CatalogAuthoringTestData.Shop, [Permissions.StockCount, Permissions.StockSourceRead]);
        var incoming = await service.ReadReturnsAsync(actor, caseId, true);
        var intake = await service.ReceiveReturnAsync(actor, caseId, incoming.Revision, incoming.Binding,
            new("Kemasan utuh sintetis diterima untuk pemeriksaan", [new(line.ReceiptId.Value, line.SkuId.Value, units, "sealed")]), CatalogPriceTestData.Meta("lot-browser-intake"));
        var returnId = intake["returnId"].AsInt64; var inspection = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        await service.InspectReturnAsync(actor, caseId, returnId, 0, inspection.Binding,
            new("Varian dan kemasan utuh diperiksa secara sintetis", [new(line.ReceiptId.Value, line.SkuId.Value, units,
                "matched", "sealed", "Identitas varian sesuai pengiriman asli", "LABEL-FISIK-SINTETIS")]), CatalogPriceTestData.Meta("lot-browser-inspection"));
        var review = await service.ReadLotVerificationAsync(actor, caseId, returnId);
        var choice = Assert.Single(Assert.Single(review.Lines).Choices);
        var reader = await catalog.ActorAsync(955810, CatalogAuthoringTestData.Shop, Permissions.CaseRead);
        var foreign = await catalog.ActorAsync(955820, CatalogAuthoringTestData.OtherOwner, Permissions.CaseRead, Permissions.CaseManage, Permissions.StockCount);
        await PersonTestData.SeedDistinctPeopleAsync(invoice.Order.Database, clock, reader.UserId, foreign.UserId);
        clock.UseSystemClock = true;
        var issued = new Dictionary<long, SessionIssue>();
        foreach (var person in new[] { actor, reader, foreign }) {
            await invoice.Rows("identities").UpdateOneAsync(new BsonDocument("_id", person.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var now = clock.UtcNow;
            var provider = new ProviderIdentityProof(person.ProviderIssuer, person.ProviderSubject, "return-lot-" + person.UserId + "@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            issued[person.UserId] = await new MongoUnitOfWork(catalog.Client).ExecuteAsync((tx, ct) => catalog.Sessions.CreateAsync(tx,
                new(person.UserId, 1, person.GrantVersion, person.ProviderIssuer, person.ProviderSubject), provider, "synthetic-return-lot-provider", ct));
            if (!await invoice.Rows("accountProfiles").Find(new BsonDocument("_id", person.UserId)).AnyAsync())
                await invoice.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", person.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                    { "displayName", "Synthetic returned-lot participant " + person.UserId }, { "verifiedEmail", "return-lot-" + person.UserId + "@example.invalid" },
                    { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } });
        }
        var receiptBefore = await invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var inspectionBefore = await invoice.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync();
        var poolId = Assert.Single(AfterSalesStockSourceEvents.ProvenancePools(receiptBefore)); Assert.Equal(choice.PoolId.Value, poolId);
        var stockBefore = await StockAndMoneyAsync(data);
        var poolBefore = await invoice.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        var headBefore = await invoice.Rows(AfterSalesStockSourceRecords.Heads).Find(new BsonDocument("_id", poolId)).SingleAsync();
        var transitionsBefore = new BsonArray(await invoice.Rows(AfterSalesStockSourceRecords.Transitions).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var auditsBefore = await invoice.Rows("auditEvents").Find(new BsonDocument()).ToListAsync();
        var commandsBefore = await invoice.Rows("idempotencyRecords").Find(new BsonDocument()).ToListAsync();
        var effectsBefore = await invoice.Rows("outboxEvents").Find(new BsonDocument()).ToListAsync();
        Assert.False(await invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument()).AnyAsync());
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = invoice.Order.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.RemoveAll<InvoiceService>(); services.AddSingleton(invoice.Service);
            services.RemoveAll<AfterSalesCaseService>(); services.AddSingleton(service);
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=UnitToko returned-lot rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var directory = Path.Combine(root, "tmp", "production", "invoice-browser-results"); Directory.CreateDirectory(directory);
        var artifact = "return-lot-" + Guid.NewGuid().ToString("N"); var proofPath = Path.Combine(directory, artifact + "-proof.json");
        var input = new { returnLot = true, apiAddress, origin, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, proofPath, financeToken = issued[actor.UserId].OpaqueToken,
            readerToken = issued[reader.UserId].OpaqueToken, foreignToken = issued[foreign.UserId].OpaqueToken,
            actorId = actor.UserId.ToString(), readerId = reader.UserId.ToString(), foreignId = foreign.UserId.ToString(),
            caseId = caseId.ToString(), returnId = returnId.ToString(), invoiceId = data.Issued.Id.ToString(),
            receiptId = line.ReceiptId.ToString(), skuId = line.SkuId.ToString(), poolId = poolId.ToString(), originalLotId = choice.OriginalLotId.ToString(),
            productName = line.ProductName, packageLabel = choice.PackageLabel, baseUnitCode = choice.BaseUnitCode, baseUnitsPerPackage = units };
        var logPath = Path.Combine(directory, artifact + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-invoice-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Returned-lot browser child did not start");
        async Task CollectAsync(StreamReader stream) { while (await stream.ReadLineAsync() is { } text) lock (lines) { lines.AppendLine(text); File.AppendAllText(logPath, text + Environment.NewLine); } }
        var stdout = CollectAsync(process.StandardOutput); var stderr = CollectAsync(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); await Task.WhenAll(stdout, stderr);
            output.WriteLine(lines.ToString()); Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains("REAL_RETURN_LOT_BROWSER_PASS", lines.ToString());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + logPath); }
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(proofPath)); var observed = proof.RootElement;
        Assert.True(observed.GetProperty("localSyntheticOnly").GetBoolean()); Assert.False(observed.GetProperty("hostedProviderAcceptance").GetBoolean());
        Assert.True(observed.GetProperty("actualBrowserApiMongo").GetBoolean()); Assert.Equal(1, observed.GetProperty("actualSuccessfulWriteCount").GetInt32());
        foreach (var image in observed.GetProperty("screenshotPaths").EnumerateArray()) { Assert.True(Path.IsPathFullyQualified(image.GetString()!)); Assert.True(File.Exists(image.GetString())); }
        Assert.Equal(4, observed.GetProperty("screenshotPaths").GetArrayLength());
        var key = observed.GetProperty("originalKey").GetString()!;
        var finding = await invoice.Rows(ReturnedLotVerificationRecords.Collection).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(returnId, finding["_id"].AsInt64); Assert.Equal(actor.UserId, finding["actorId"].AsInt64); Assert.Equal(key, finding["requestKey"].AsString);
        Assert.Equal("LABEL-RETUR-BROWSER-SINTETIS", finding["input"]["verificationReference"].AsString);
        Assert.True(finding["input"]["confirmPhysicalOriginalLotMatch"].AsBoolean);
        var selected = Assert.Single(finding["input"]["lines"].AsBsonArray);
        Assert.Equal(line.ReceiptId.Value, selected["receiptId"].AsInt64); Assert.Equal(line.SkuId.Value, selected["skuId"].AsInt64);
        Assert.Equal(choice.OriginalLotId.Value, selected["originalLotId"].AsInt64); Assert.Equal((long)units, selected["verifiedBaseUnits"].AsInt64);
        Assert.Equal(receiptBefore, await invoice.Rows(ReturnQuarantineLedger.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync());
        Assert.Equal(inspectionBefore, await invoice.Rows(ReturnInspectionRecords.Collection).Find(new BsonDocument("_id", returnId)).SingleAsync());
        Assert.Equal("unverified", receiptBefore["lotIdentity"].AsString); Assert.Equal("unverified", inspectionBefore["lotIdentity"].AsString);
        Assert.Equal(ReturnedLotVerificationRecords.Audit(finding), await invoice.Rows("auditEvents").Find(new BsonDocument("_id", finding["auditId"])).SingleAsync());
        var operation = ReturnedLotVerificationRecords.Operation;
        var command = await invoice.Rows("idempotencyRecords").Find(new BsonDocument("operation", operation)).SingleAsync();
        Assert.Equal(ReturnedLotVerificationRecords.Result(finding), command["result"]); Assert.Equal(key, command["key"]);
        var effect = await invoice.Rows("outboxEvents").Find(new BsonDocument("kind", operation)).SingleAsync();
        Assert.Equal(ReturnedLotVerificationRecords.Result(finding), effect["payload"]); Assert.Equal(key, effect["requestKey"]);
        Assert.Equal(returnId.ToString(), observed.GetProperty("recoveredResult").GetProperty("returnId").GetString());
        Assert.Equal(data.Issued.Id.ToString(), observed.GetProperty("recoveredResult").GetProperty("invoiceId").GetString());
        var poolAfter = await invoice.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        var expectedPool = poolBefore.DeepClone().AsBsonDocument; expectedPool["fence"] = poolBefore["fence"].AsInt64 + 1;
        Assert.Equal(expectedPool, poolAfter); // one real source CAS, with active/revision unchanged
        var added = await invoice.Rows(AfterSalesStockSourceRecords.Transitions).Find(new BsonDocument("sourceKind", "verifyLot")).SingleAsync();
        Assert.Equal(returnId, added["sourceId"].AsInt64); Assert.Equal(finding["auditId"], added["sourceAuditId"]);
        Assert.Equal(poolBefore["fence"], added["beforePoolFence"]); Assert.Equal(poolAfter["fence"], added["afterPoolFence"]);
        var expectedHead = headBefore.DeepClone().AsBsonDocument; expectedHead["revision"] = headBefore["revision"].AsInt64 + 1;
        expectedHead["fence"] = headBefore["fence"].AsInt64 + 1; expectedHead["lastTransitionId"] = added["_id"];
        expectedHead["lastHash"] = added["transitionHash"]; expectedHead["updatedAt"] = added["recordedAt"];
        Assert.Equal(expectedHead, await invoice.Rows(AfterSalesStockSourceRecords.Heads).Find(new BsonDocument("_id", poolId)).SingleAsync());
        var allTransitions = await invoice.Rows(AfterSalesStockSourceRecords.Transitions).Find(new BsonDocument()).ToListAsync();
        Assert.Equal(transitionsBefore.Count + 1, allTransitions.Count); foreach (var prior in transitionsBefore) Assert.Contains(prior.AsBsonDocument, allTransitions);
        var unit = new MongoUnitOfWork(catalog.Client); var records = new AfterSalesStockSourceRecords(invoice.Order.Database);
        var owner = new AfterSalesStockSourceEvents(invoice.Order.Database, new(invoice.Order.Database), clock);
        var history = await unit.ExecuteAsync((tx, ct) => records.HistoryAsync(tx, owner, poolId, ct));
        Assert.Contains(returnId, history.Open); Assert.Equal(expectedHead, history.Head);
        Assert.Equal(stockBefore, await StockAndMoneyAsync(data));
        foreach (var prior in auditsBefore) Assert.Equal(prior, await invoice.Rows("auditEvents").Find(new BsonDocument("_id", prior["_id"])).SingleAsync());
        foreach (var prior in commandsBefore) Assert.Equal(prior, await invoice.Rows("idempotencyRecords").Find(new BsonDocument("_id", prior["_id"])).SingleAsync());
        foreach (var prior in effectsBefore) Assert.Equal(prior, await invoice.Rows("outboxEvents").Find(new BsonDocument("_id", prior["_id"])).SingleAsync());
        Assert.Equal(commandsBefore.Count + 1, await invoice.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(effectsBefore.Count + 1, await invoice.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument()));
    }
    private async Task<PaymentDraftData> WholePackageAsync() {
        // Exact existing M95 whole-package fixture command route. Keep the first
        // signed six-loose receipt intact and produce a distinct full package.
        var data = await PaymentDraftData.CreateAsync(fixture);
        try {
            var dispatch = data.Invoice.Dispatch;
            await dispatch.PrepareAsync(1, dispatch.Prepared.PreparationRevision, "lot-browser-whole-prepare");
            var shipment = (await dispatch.DispatchAsync(await dispatch.PreviewAsync(), "lot-browser-whole-dispatch"))["shipmentId"].AsInt64;
            var document = await ShipmentDocumentTests.Service(dispatch).IssueAsync(dispatch.Picker, shipment, 1, CatalogPriceTestData.Meta("lot-browser-document"));
            var recipients = ShipmentReceiptTests.Service(dispatch);
            var challenge = await recipients.IssueChallengeAsync(dispatch.Courier, shipment, document.Id.Value, dispatch.Order.Buyer.UserId, CatalogPriceTestData.Meta("lot-browser-challenge"));
            var review = await recipients.ReviewAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!);
            var receipt = await recipients.ConfirmAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
                [new(review.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)], CatalogPriceTestData.Meta("lot-browser-recipient"));
            var selected = ImmutableArray.Create(new InvoiceReceiptSelection(receipt.ShipmentId, receipt.Id));
            var preview = await data.Invoice.Service.PreviewAsync(data.Invoice.Finance, selected);
            var issued = await data.Invoice.Service.IssueAsync(data.Invoice.Finance, selected, preview.Review, CatalogPriceTestData.Meta("lot-browser-invoice"));
            Assert.Equal(Assert.Single(issued.Detail.Lines).BaseUnitsPerPackage, Assert.Single(issued.Detail.Lines).AcceptedBaseUnits);
            return new(data.Invoice, issued);
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task<BsonDocument> StockAndMoneyAsync(PaymentDraftData data) {
        var state = new BsonDocument();
        foreach (var name in new[] { "stockBalances", "lots", "movements", "reservations", InvoiceBson.Invoices, InvoiceBson.Allocations, "journalEntries",
            "paymentDeclarations", "paymentSubmissions", "paymentAllocations", "cashMovements", "bankReceipts", "refundPayouts", "refundSettlements", "returnResolutionRequests" }) {
            var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            // Existing ownership reads/command fences are the only normalized
            // stock fields; quantities, revisions, lot identities and money exact.
            if (name is "stockBalances" or "lots" or "movements" or "reservations") foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); }
            state[name] = new BsonArray(rows);
        }
        return state;
    }
    private static string Root() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "frontend", "scripts", "verify-invoice-browser.mjs"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Authoritative browser source was not found");
    }
}

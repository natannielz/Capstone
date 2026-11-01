using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Ordering;
using Xunit.Abstractions;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Tests.Journeys;

/// <summary>The golden journey (docs/quality/website-ready/journeys.md §5): one synthetic world, each step done in a real
/// browser by its own persona, asserting the visible result and the record's typed number. Personas hold only the grants
/// their step needs; identities, contacts and the courier roster are local synthetic fixtures, not provider acceptance.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class GoldenJourneyBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    /// <summary>A1: guest sees no prices; pemohon adds to the keranjang and checks out; penyetuju-cabang approves spending;
    /// peninjau-toko accepts; petugas-gudang prepares; petugas-kirim dispatches with a kurir; petugas-gudang issues the
    /// Surat Jalan; the kurir makes the receipt QR; pemohon counts and confirms receipt.</summary>
    [LocalStockBrowserFact]
    public async Task Golden_order_to_receipt_across_personas_shows_each_result_and_order_number() =>
        await OrderWorldAsync(fixture, output, "order-to-receipt", "GOLDEN_JOURNEY_ORDER_TO_RECEIPT_PASS", null, async (data, actors, lines) => {
            // The browser's work is in Mongo exactly once, by the persona that owns each step.
            var order = await data.Rows(B.Orders).Find(new BsonDocument()).SingleAsync();
            Assert.Equal(actors["pemohon"].UserId, order["createdBy"].AsInt64);
            Assert.Equal(2, (await data.Rows("orderDecisions").Find(new BsonDocument()).ToListAsync()).Count);
            var shipment = await data.Rows("shipments").Find(new BsonDocument()).SingleAsync();
            Assert.Equal(actors["petugas-kirim"].UserId, shipment["dispatchedBy"].AsInt64); Assert.Equal(actors["kurir"].UserId, shipment["courierId"].AsInt64);
            var receipt = await data.Rows("shipmentReceipts").Find(new BsonDocument()).SingleAsync();
            Assert.Equal(actors["pemohon"].UserId, receipt["recipientId"].AsInt64); Assert.False(receipt["hasException"].AsBoolean);
            Assert.Contains("PSN-" + order["_id"].AsInt64.ToString("D6", CultureInfo.InvariantCulture), lines);
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", B.Operation)));
        });

    /// <summary>A2 (journeys.md §5.2 A10–A13): the received order from A1 is invoiced and paid. keuangan-tagihan proposes the invoice
    /// release and pemeriksa-pembayaran approves it; keuangan-tagihan issues the invoice; pemohon pays with original evidence;
    /// pemeriksa-pembayaran verifies against the bank's credit and allocates it to the invoice; pemohon sees the allocation.</summary>
    [LocalStockBrowserFact]
    public async Task Golden_invoice_release_to_verified_payment_across_personas_shows_each_result_and_invoice_number()
    {
        // Production-like: a deployed API runs with a provenance seal ring, so the receipt and dispatch are sealed as they are written;
        // without it every read takes the full-proof fallback, which is what once exceeded the 30 s browser bound (F-004).
        var ownsRing = ProvenanceSealRing.Current is null;
        if (ownsRing) ProvenanceSealRing.Configure(SealKeyRing.ForKeys("golden-invoice", new Dictionary<string, byte[]> { ["golden-invoice"] = RandomNumberGenerator.GetBytes(32) }));
        try {
            await OrderWorldAsync(fixture, output, "invoice-to-payment", "GOLDEN_JOURNEY_INVOICE_TO_PAYMENT_PASS", null, VerifyInvoiceToPaymentAsync, GoldenInvoiceWorld.PrepareAsync);
        } finally { if (ownsRing) ProvenanceSealRing.Configure(null); }
    }

    private static async Task VerifyInvoiceToPaymentAsync(GenuineFiniteDispatchTestData data, Dictionary<string, ActorContext> actors, string lines)
    {
        var maker = actors["keuangan-tagihan"].UserId; var checker = actors["pemeriksa-pembayaran"].UserId; var buyer = actors["pemohon"].UserId;
        var events = await data.Rows(InvoiceReleaseBson.Events).Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync();
        Assert.Equal(new[] { "propose", "approve" }, events.Select(row => row["action"].AsString)); Assert.Equal(new[] { maker, checker }, events.Select(row => row["actorId"].AsInt64));
        var invoice = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument()).SingleAsync(); Assert.Equal(maker, invoice["issuedBy"].AsInt64);
        var binding = await data.Rows(InvoiceReleaseBson.Bindings).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(invoice["_id"], binding["invoiceId"]); Assert.Equal(events[1]["_id"], binding["decisionId"]);
        var number = string.Join('/', invoice["numberEntity"].AsInt64, invoice["numberChannel"].AsInt64, invoice["numberYear"].AsInt64, invoice["number"].AsInt64);
        Assert.Contains("\"invoice\":\"" + number + "\"", lines);
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync(); Assert.Equal(buyer, submission["recordedBy"].AsInt64);
        var head = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument()).SingleAsync(); Assert.Equal("submitted", head["state"].AsString);
        var verification = await data.Rows(PaymentVerificationBson.Decisions).Find(new BsonDocument()).SingleAsync(); Assert.Equal(checker, verification["actorId"].AsInt64);
        Assert.Equal(head["_id"], verification["declarationId"]);
        var allocation = await data.Rows(PaymentAllocationBson.Events).Find(new BsonDocument()).SingleAsync(); Assert.Equal(checker, allocation["actorId"].AsInt64);
        Assert.Equal(invoice["_id"].AsInt64, Assert.Single(allocation["lines"].AsBsonArray)["invoiceId"].AsInt64);
        // One committed command per step: no duplicate receipt for any of the money-moving operations.
        foreach (var operation in new[] { InvoiceReleaseBson.Prefix + "propose", InvoiceReleaseBson.Prefix + "approve", InvoiceBson.Operation, PaymentDraftBson.Operation,
            "billing.payment.submit", PaymentVerificationBson.Operation, PaymentAllocationBson.Operation })
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
        Assert.Equal(1L, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));

        // Money end state: every figure traces back to the one invoice total, through the declaration, the bank credit, the
        // verification, the allocation and both balance heads.
        static decimal Dec(BsonValue value) => Decimal128.ToDecimal(value.AsDecimal128);
        var total = Dec(invoice["totals"]["total"]); Assert.True(total > 0m);
        var declared = await data.Rows(PaymentDraftBson.Versions).Find(new BsonDocument("declarationId", head["_id"])).Sort(new BsonDocument("revision", -1)).FirstAsync();
        Assert.Equal(total, Dec(declared["input"]["declaredAmount"]));
        var credit = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(total, Dec(credit["amount"])); Assert.False(credit["journalId"].IsBsonNull, "The bank credit is posted once as a cash journal");
        Assert.Equal(1L, await data.Rows(BankReceiptBson.Confirmations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", BankReceiptBson.Operation)));
        Assert.Equal(credit["_id"], verification["receiptId"]);
        Assert.Equal(verification["_id"], allocation["verificationId"]); Assert.Equal(credit["_id"], allocation["receiptId"]);
        Assert.Equal(total, Dec(allocation["amount"])); Assert.Equal(0m, Dec(allocation["unapplied"]));
        var line = Assert.Single(allocation["lines"].AsBsonArray); Assert.Equal(total, Dec(line["amount"])); Assert.Equal(0m, Dec(line["remaining"]));
        var owed = await data.Rows(PaymentAllocationBson.Invoices).Find(new BsonDocument("_id", invoice["_id"])).SingleAsync();
        Assert.Equal(total, Dec(owed["total"])); Assert.Equal(total, Dec(owed["paid"])); Assert.Equal(0m, Dec(owed["credited"]));
        var funds = await data.Rows(PaymentAllocationBson.Receipts).Find(new BsonDocument("_id", credit["_id"])).SingleAsync();
        Assert.Equal(total, Dec(funds["total"])); Assert.Equal(total, Dec(funds["allocated"])); Assert.Equal(0m, Dec(funds["reservedForRefund"]));
        // The checker's correction of the OCR reading is the saved review version, and it holds the invoice total.
        var correction = await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument("declarationId", head["_id"])).SingleAsync();
        Assert.Equal(checker, correction["recordedBy"].AsInt64);
        var amountField = correction["fields"].AsBsonArray.Single(field => field["field"].AsString == "amount");
        Assert.Equal(total, decimal.Parse(amountField["value"].AsString, CultureInfo.InvariantCulture));
        Assert.Contains("Pengajuan " + head["_id"].AsInt64.ToString(CultureInfo.InvariantCulture), lines);
    }

    /// <summary>Extra personas, service wiring and named fixture actions a stage adds to the order world.</summary>
    internal sealed record WorldExtension(Action<IServiceCollection> Services, Func<string, Task<object?>> Change);

    /// <summary>The order-to-cash world: stock with value, prices, spending and commercial policy, approved admissions and a
    /// courier roster, with one persona per step. <paramref name="change"/> adds named world changes for the browser.</summary>
    internal static async Task OrderWorldAsync(MongoFixture fixture, ITestOutputHelper output, string stage, string marker,
        Func<GenuineFiniteDispatchTestData, string, Task<object?>>? change, Func<GenuineFiniteDispatchTestData, Dictionary<string, ActorContext>, string, Task> verify,
        Func<GenuineFiniteDispatchTestData, Dictionary<string, ActorContext>, Task<WorldExtension>>? extend = null)
    {
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, cogs: true,
            createOrder: false, reviewedSafetyBufferBaseUnits: 0, useSystemClock: true,
            // An extended world invoices and takes payment, so the book carries the customer receivable and cash mappings.
            includeCustomerMappings: extend is not null, includeCustomerCashMappings: extend is not null);
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        // The buyer template's own read/receipt grants; the fixture buyer already holds catalog/order submit/read/cancel.
        await data.Base.AddGrantAsync(data.Buyer, 719101, CatalogAuthoringTestData.Audience, [Permissions.ShipmentRead, Permissions.ReceiptAccept]);
        await data.Base.AddGrantAsync(data.Courier, 719102, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), [Permissions.DeliveryEvidence]);
        // One persona per step instead of the fixture's combined store dispatcher.
        var reviewer = await data.Base.AddActorAsync(719110, shop, Permissions.OrderAccept, Permissions.OrderRead);
        var warehouse = await data.Base.AddActorAsync(719111, shop, Permissions.ShipmentPrepare, Permissions.ShipmentRead);
        var shipping = await data.Base.AddActorAsync(719112, shop, Permissions.ShipmentDispatch, Permissions.ShipmentAssign, Permissions.ShipmentRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, reviewer.UserId, warehouse.UserId, shipping.UserId);
        var actors = new Dictionary<string, ActorContext> {
            ["pemohon"] = data.Buyer, ["penyetuju-cabang"] = data.Spender, ["peninjau-toko"] = reviewer,
            ["petugas-gudang"] = warehouse, ["petugas-kirim"] = shipping, ["kurir"] = data.Courier
        };
        var extension = extend is null ? null : await extend(data, actors);
        foreach (var actor in actors.Values) if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync()) {
            var at = data.Clock.UtcNow;
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Peserta jalur emas " + actor.UserId }, { "verifiedEmail", "golden-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", at.UtcDateTime }, { "updatedAt", at.UtcDateTime } });
        }
        async Task<SessionIssue> Issue(string name) {
            actors[name] = await data.IssueExistingSessionAsync(actors[name]);
            return data.Catalog.Issued[actors[name].UserId];
        }
        var tokens = new Dictionary<string, string>(); foreach (var name in actors.Keys.ToArray()) tokens[name] = (await Issue(name)).OpaqueToken;
        var sku = await data.Rows("skus").Find(new BsonDocument("_id", data.SkuId)).SingleAsync();
        var lot = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(0L, await data.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));

        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<MongoClient>(); services.AddSingleton((MongoClient)data.Database.Client);
            services.RemoveAll<IMongoDatabase>(); services.AddSingleton(data.Database);
            services.RemoveAll<MongoUnitOfWork>(); services.AddSingleton(data.Unit);
            services.RemoveAll<IdAllocator>(); services.AddSingleton(new IdAllocator(data.Database));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Catalog.Sessions);
            extension?.Services(services);
            services.AddSingleton<IStartupFilter>(new RenewControl(controlKey, async name => actors.ContainsKey(name) ? (await Issue(name)).OpaqueToken : null,
                change is null ? extension?.Change : action => change(data, action)));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var prefix = "golden-" + stage.Replace("-", "") + "-" + Guid.NewGuid().ToString("N");
        var config = Hosting(apiAddress, playwright!, buildPath, prefix, controlKey, new {
            stage, tokens,
            actorIds = actors.ToDictionary(pair => pair.Key, pair => pair.Value.UserId.ToString(CultureInfo.InvariantCulture)),
            skuId = data.SkuId.ToString(CultureInfo.InvariantCulture), publicId = sku["publicId"].AsString, skuCode = sku["sku"].AsString,
            lotId = lot.Balance.Key.LotId.ToString(), branch = "10:11:" });
        var lines = await RunScriptAsync(output, "verify-golden-journey-browser.mjs", root, node!, config, prefix, marker, TimeSpan.FromMinutes(15));
        await verify(data, actors, lines);
    }

    /// <summary>B: pengadaan requests catalog packages (PP-); penyetuju-pengadaan approves; pengadaan proposes the supplier
    /// order and penyetuju-pengadaan approves it; pengadaan records the supplier's arrival notice and petugas-gudang records
    /// the koli physically received against the order (SP-).</summary>
    [LocalStockBrowserFact]
    public async Task Golden_procurement_request_to_received_goods_across_personas_shows_each_result_and_number()
    {
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var suppliers = await UnitToko.Tests.Procurement.SupplierTestData.CreateAsync(fixture, useSystemClock: true);
        var proposedSupplier = await suppliers.ProposeAsync("golden-supplier");
        var observation = await suppliers.ObserveAsync(proposedSupplier, "golden-supplier-observation");
        var supplier = await suppliers.DecideAsync(proposedSupplier, "golden-supplier-approved", observation["recordId"].AsInt64);
        var procurement = suppliers.Procurement; var scope = UnitToko.Tests.Procurement.ProcurementRequestTestData.Scope;
        var actors = new Dictionary<string, ActorContext> {
            ["pengadaan"] = await suppliers.Base.AddActorAsync(719201, scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare),
            ["penyetuju-pengadaan"] = await suppliers.Base.AddActorAsync(719202, scope, Permissions.ProcurementRead, Permissions.ProcurementApprove),
            ["petugas-gudang"] = await suppliers.Base.AddActorAsync(719203, scope, Permissions.ProcurementRead, Permissions.ProcurementContainerReceive),
        };
        await PersonTestData.SeedDistinctPeopleAsync(suppliers.Database, suppliers.Clock, [.. actors.Values.Select(actor => actor.UserId)]);
        var sessions = new MongoIdentitySessionStore(suppliers.Database, suppliers.Unit, suppliers.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var tokens = new Dictionary<string, string>();
        async Task<string?> IssueProcurement(string name) {
            if (!actors.TryGetValue(name, out var actor)) return null;
            var now = suppliers.Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "golden-" + actor.UserId + "@example.invalid", true, "fixture-connection", now, now, "otp", "fixture-policy");
            var issued = await suppliers.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-golden-provider", ct));
            return tokens[name] = issued.OpaqueToken;
        }
        foreach (var (name, actor) in actors) {
            await suppliers.Rows("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            await suppliers.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Peserta pengadaan " + name }, { "verifiedEmail", "golden-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", suppliers.Clock.UtcNow.UtcDateTime }, { "updatedAt", suppliers.Clock.UtcNow.UtcDateTime } });
            await IssueProcurement(name);
        }
        var sku = await suppliers.Rows("skus").Find(new BsonDocument("_id", procurement.Sku.Id.Value)).SingleAsync();
        var definition = await suppliers.Rows("variantDefinitions").Find(new BsonDocument("_id", procurement.Sku.CurrentDefinitionId!.Value.Value)).SingleAsync();
        var date = UnitToko.Modules.Accounting.AccountingCalendar.BusinessDate(suppliers.Clock.UtcNow);
        var until = DateTimeOffset.FromUnixTimeSeconds(suppliers.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60).AddDays(1);

        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = suppliers.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(suppliers.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            services.AddSingleton<IStartupFilter>(new RenewControl(controlKey, IssueProcurement));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var prefix = "golden-b-" + Guid.NewGuid().ToString("N");
        var config = Hosting(apiAddress, playwright!, buildPath, prefix, controlKey, new {
            stage = "procurement-to-receipt", tokens,
            actorIds = actors.ToDictionary(pair => pair.Key, pair => pair.Value.UserId.ToString(CultureInfo.InvariantCulture)),
            location = "100:101:102:103", locationLabel = "Synthetic cooperative · Synthetic fulfillment shop · Synthetic dispatch warehouse · Synthetic retail channel",
            skuId = procurement.Sku.Id.Value.ToString(CultureInfo.InvariantCulture), skuCode = sku["sku"].AsString, packageLabel = definition["packageLabel"].AsString,
            supplierId = supplier["supplierId"].AsInt64.ToString(CultureInfo.InvariantCulture), supplierName = UnitToko.Tests.Procurement.SupplierTestData.Details.DisplayName,
            businessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dueDate = date.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            validUntilWib = until.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) });
        await RunAsync(root, node!, config, prefix, "GOLDEN_JOURNEY_PROCUREMENT_TO_RECEIPT_PASS", TimeSpan.FromMinutes(15));

        var request = await suppliers.Rows("procurementRequests").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(actors["pengadaan"].UserId, request["actorId"].AsInt64);
        var decision = await suppliers.Rows("procurementRequestDecisions").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(actors["penyetuju-pengadaan"].UserId, decision["actorId"].AsInt64);
        Assert.Equal(1L, await suppliers.Rows("purchaseOrders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await suppliers.Rows("procurementArrivalNotices").CountDocumentsAsync(new BsonDocument()));
        var intake = await suppliers.Rows("procurementContainerIntakes").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(actors["petugas-gudang"].UserId, intake["actorId"].AsInt64);
    }

    internal static object Hosting(string apiAddress, string playwright, string buildPath, string prefix, string controlKey, object journey) {
        const string origin = "https://portal.unit-toko.co.id";
        using var rsa = RSA.Create(2048); var certificate = new CertificateRequest("CN=Local golden journey rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(origin).Host); certificate.CertificateExtensions.Add(names.Build());
        using var tls = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return new { apiAddress, origin, controlKey, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, buildId = File.ReadAllText(buildPath).Trim(), proofPrefix = prefix, journey };
    }

    internal Task<string> RunAsync(string root, string node, object config, string prefix, string marker, TimeSpan limit) =>
        RunScriptAsync(output, "verify-golden-journey-browser.mjs", root, node, config, prefix, marker, limit);
    internal static async Task<string> RunScriptAsync(ITestOutputHelper output, string script, string root, string node, object config, string prefix, string marker, TimeSpan limit) {
        var directory = Path.Combine(root, "tmp", "production", "golden-journey-browser-results"); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, prefix + "-native-child.log"); var lines = new StringBuilder();
        var start = new ProcessStartInfo(node) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", script));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Golden journey browser child did not start");
        async Task Read(StreamReader stream) { while (await stream.ReadLineAsync() is { } line) lock (lines) { lines.AppendLine(line); File.AppendAllText(log, line + Environment.NewLine); } }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(limit); await Task.WhenAll(stdout, stderr); output.WriteLine(lines.ToString());
            Assert.True(process.ExitCode == 0, lines.ToString()); Assert.Contains(marker, lines.ToString());
        } finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); output.WriteLine("Actual browser child log: " + log);
        }
        return lines.ToString();
    }

    /// <summary>Fresh sessions for a named persona (step-up freshness before a decision), plus named world changes a test
    /// declares (for example an independently approved new price); nothing else is exposed.</summary>
    internal sealed class RenewControl(string secret, Func<string, Task<string?>> renew, Func<string, Task<object?>>? change = null) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/golden")) { await invoke(); return; }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != secret) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/golden/".Length..];
                if (action.StartsWith("renew-", StringComparison.Ordinal) && await renew(action["renew-".Length..]) is { } token) {
                    await context.Response.WriteAsJsonAsync(new { token }); return;
                }
                if (change is not null && await change(action) is { } changed) { await context.Response.WriteAsJsonAsync(changed); return; }
                context.Response.StatusCode = 404;
            }); next(app);
        };
    }

    internal static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the golden journey from a checkout");
    }
}

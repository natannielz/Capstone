using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using Xunit.Abstractions;
using Data = UnitToko.Tests.Inventory.StockReleaseServiceTests.Data;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Tests.Inventory;

/// <summary>Opt-in signed local OIDC/browser/API/Mongo proof. Real owners create
/// receipt, quality, posted value, activation, release and reviewed rule before the browser.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    [LocalStockBrowserFact]
    public async Task Browser_real_lot_conversion_committed_reply_loss_original_key_recovery_and_stock_value_are_durable() {
        using var timing = new StockReleaseTestDiagnostics(output);
        var root = RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node), "Set the actual local Node24 executable.");
        Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright), "Set the pinned local Playwright package.json.");
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var data = await Data.CreateAsync(fixture, stage => output.WriteLine("FIXTURE " + stage), useSystemClock: true);
        var released = await data.ProposeAsync("browser-conversion-release"); await data.DecideAsync(released["proposalId"].AsInt64, "browser-conversion-release-approved");
        var procurement = data.Valuation.Inspection.Receiving.Procurement;
        var sku = await data.Rows("skus").Find(new BsonDocument("_id", procurement.Sku.Id.Value)).SingleAsync();
        var family = await data.Rows("productFamilies").Find(new BsonDocument("_id", sku["familyId"])).SingleAsync();
        var loose = await procurement.Catalog.CreateSkuAsync(procurement.Author, new(new(family["_id"].AsInt64), family["revision"].AsInt64,
            "BROWSER-CONVERSION-LOOSE", sku["physicalItemKey"].AsString, CatalogAuthoringTestData.Definition(1, "Satuan lepas") with { PackageKind = "loose" }), Data.Meta("browser-conversion-loose"));
        var maker = await data.Valuation.Base.AddActorAsync(104201, ProcurementRequestTestData.Scope, Permissions.StockConvert);
        var reviewer = await data.Valuation.Base.AddActorAsync(104202, ProcurementRequestTestData.Scope, Permissions.StockConvert);
        var foreign = await data.Valuation.Base.AddActorAsync(104203, new(GrantScopeKind.FulfillmentWarehouse, 100,
            ShopId: procurement.ForeignShopId, WarehouseId: procurement.ForeignWarehouseId, ChannelId: 103), Permissions.StockConvert);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, reviewer.UserId, foreign.UserId);
        var rules = new PackConversionRuleService(data.Database, data.Unit, new(data.Database), data.Clock);
        var ruleInput = new PackConversionRuleInput(ProcurementRequestTestData.Scope, procurement.Sku.Id.Value, sku["revision"].AsInt64, sku["currentDefinitionId"].AsInt64,
            loose.Id.Value, loose.Version, loose.CurrentDefinitionId!.Value.Value, 1, 12);
        var rulePreview = await rules.PreviewRuleAsync(maker, ruleInput);
        var proposed = await rules.ProposeRuleAsync(maker, ruleInput, rulePreview["binding"].AsString, Data.Meta("browser-conversion-rule-proposed"));
        var reviewed = await rules.PreviewDecisionAsync(reviewer, proposed["proposalId"].AsInt64, PackConversionRuleDecision.Approve);
        var approved = await rules.DecideRuleAsync(reviewer, proposed["proposalId"].AsInt64, 1, PackConversionRuleDecision.Approve,
            reviewed["binding"].AsString, Data.Meta("browser-conversion-rule-approved"));
        var source = await data.BalanceAsync(); Assert.Equal((3L, 2L), (source["onHand"].AsInt64, source["held"].AsInt64));
        var original = await Preserved(data); var originalLot = await data.Rows("lots").Find(new BsonDocument("_id", source["lotId"])).SingleAsync();
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName; settings["Identity:TrustedProxies:0"] = "127.0.0.1";
        settings["Logging:LogLevel:Default"] = "Warning";
        var options = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(Auth0Options.SectionName).Get<Auth0Options>()!;
        foreach (var (fixtureActor, account) in new[] { (maker, "maker"), (foreign, "foreign") })
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", fixtureActor.UserId), Builders<BsonDocument>.Update
                .Set("issuer", options.Issuer).Set("subject", "auth0|pack-execution-" + account).Set("securityVersion", 1L));
        var selection = new ProviderSelection(); var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var provider = new ProviderHandlerTests.FakeProvider(options, new SystemClock());
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.Configure<OpenIdConnectOptions>(Auth0Options.Scheme, value => value.Backchannel = new HttpClient(provider, disposeHandler: false));
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
            services.AddSingleton<IStartupFilter>(new ObserveProvider(provider, options.Issuer, selection));
            services.AddSingleton<IStartupFilter>(new FixtureControl(data, maker.UserId, controlKey, selection));
        }));
        factory.UseKestrel(value => value.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal("127.0.0.1", new Uri(apiAddress).Host);
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=UnitToko local physical conversion browser", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(new Uri(options.PublicOrigin).Host); names.AddDnsName(new Uri(options.Issuer).Host); request.CertificateExtensions.Add(names.Build());
        using var tls = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var prefix = "pack-execution-" + Guid.NewGuid().ToString("N");
        var input = new { apiAddress, origin = options.PublicOrigin, issuer = options.Issuer, pfx = Convert.ToBase64String(tls.Export(X509ContentType.Pfx, password)), password,
            playwrightPackage = playwright, makerId = maker.UserId.ToString(CultureInfo.InvariantCulture), foreignId = foreign.UserId.ToString(CultureInfo.InvariantCulture),
            ruleId = approved["ruleId"].AsInt64.ToString(CultureInfo.InvariantCulture), sourceBalanceId = source["_id"].AsInt64.ToString(CultureInfo.InvariantCulture),
            buildId = (await File.ReadAllTextAsync(buildPath)).Trim(), controlKey, proofPrefix = prefix };
        var start = new ProcessStartInfo(node!) { WorkingDirectory = Path.Combine(root, "frontend"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "frontend", "scripts", "verify-pack-conversion-execution-browser.mjs"));
        start.Environment["NODE_ENV"] = "production"; start.Environment["NEXT_TELEMETRY_DISABLED"] = "1"; start.Environment["NEXT_PUBLIC_UNIT_TOKO_RELEASE"] = "full";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Local conversion browser could not start.");
        try {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input)); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); output.WriteLine(await stdout); output.WriteLine(await stderr);
            Assert.True(process.ExitCode == 0, $"Local synthetic physical conversion browser failed: {await stdout}; {await stderr}");
            Assert.Contains("LOCAL_SYNTHETIC_PACK_EXECUTION_BROWSER_PASS", await stdout);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(provider.SawValidPkce); Assert.Equal(2, provider.TokenRequests);
        Assert.All(Directory.GetFiles(keys.Directory, "key-*.xml"), path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
        var actor = selection.MakerActor ?? throw new InvalidOperationException("Actual signed-cookie maker was not captured"); Assert.NotNull(actor.Session);
        var conversion = Assert.Single(await data.Rows(B.Conversions).Find(new BsonDocument()).ToListAsync()); Assert.Equal(maker.UserId, conversion["actorId"].AsInt64);
        Assert.Equal("committed", conversion["phase"].AsString); Assert.Equal((1L, 12L, 12L), (conversion["quantities"]["sourcePackages"].AsInt64,
            conversion["quantities"]["destinationPackages"].AsInt64, conversion["quantities"]["baseUnits"].AsInt64));
        Assert.Equal("11250.00", conversion["transferredValue"].AsString);
        var afterSource = await data.BalanceAsync(); Assert.Equal((2L, 2L, 0L, 0L), (afterSource["onHand"].AsInt64, afterSource["held"].AsInt64, afterSource["reserved"].AsInt64, afterSource["expired"].AsInt64));
        var destination = await data.Rows("stockBalances").Find(new BsonDocument("_id", conversion["destinationAfter"]["_id"])).SingleAsync();
        Assert.Equal((12L, 0L, 0L, 0L), (destination["onHand"].AsInt64, destination["held"].AsInt64, destination["reserved"].AsInt64, destination["expired"].AsInt64));
        var sourceValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", source["_id"])).SingleAsync();
        var destinationValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", destination["_id"])).SingleAsync();
        Assert.Equal((0L, "0.00"), (sourceValue["remainingPackages"].AsInt64, sourceValue["remainingValue"].AsString));
        Assert.Equal((12L, "11250.00"), (destinationValue["remainingPackages"].AsInt64, destinationValue["remainingValue"].AsString));
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument("operation", "convert")));
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, B.Operation)));
        Assert.Equal(originalLot, await data.Rows("lots").Find(new BsonDocument("_id", source["lotId"])).SingleAsync());
        var service = factory.Services.GetRequiredService<PackConversionService>();
        Assert.Equal("converted", (await service.RecoverAsync(actor, conversion["requestKey"].AsString))!["status"].AsString);
        var preserved = await Preserved(data); Assert.Equal(original, preserved);
        var directory = Path.Combine(root, "tmp", "production", "pack-execution-browser-results"); Directory.CreateDirectory(directory);
        var proofPath = Path.Combine(directory, prefix + "-mongo.json");
        await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(new { recordedUtc = DateTimeOffset.UtcNow, input.buildId,
            conversionCount = 1, movementCount = 1, conversionValueTransitionCount = 1, sourceOnHandBefore = 3, sourceOnHandAfter = 2, sourceHeldBefore = 2, sourceHeldAfter = 2,
            destinationPackages = 12, destinationValue = "11250.00", durableSha256 = Hash(await Durable(data)), originalBeforeSha256 = Hash(original), originalAfterSha256 = Hash(preserved),
            actualAuthenticatedActorReused = true, originalAcquisitionDocumentsJournalPayableUnchanged = true, noCogsPosted = true,
            localSyntheticOnly = true, externalProviderAccepted = false, stagingValidated = false, productionAccepted = false }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine("LOCAL_SYNTHETIC_PACK_EXECUTION_MONGO_PASS " + proofPath);
    }
    private sealed class ProviderSelection { internal string Account = "maker"; internal ActorContext? MakerActor; }
    private sealed class ObserveProvider(ProviderHandlerTests.FakeProvider provider, string issuer, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, invoke) => { context.Response.OnStarting(() => {
                var location = context.Response.Headers.Location.ToString();
                if (location.StartsWith(issuer + "authorize?", StringComparison.Ordinal)) {
                    provider.Challenge = QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
                    provider.Subject = "auth0|pack-execution-" + selection.Account; provider.Email = "pack-execution-" + selection.Account + "@company.co.id";
                }
                return Task.CompletedTask;
            }); return invoke(); }); next(app);
        };
    }
    private sealed class FixtureControl(Data data, long makerId, string key, ProviderSelection selection) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, invoke) => {
                if (!context.Request.Path.StartsWithSegments("/__fixture/pack-execution")) {
                    await invoke();
                    if (context.Response.StatusCode == 200 && context.Request.Path.StartsWithSegments("/api/v1/inventory/pack-conversions/executions") &&
                        context.Features.Get<ResolvedActor>()?.Actor is { } actor && actor.UserId == makerId) selection.MakerActor = actor;
                    return;
                }
                if (context.Request.Method != "POST" || context.Request.Headers["X-Fixture-Control"] != key) { context.Response.StatusCode = 404; return; }
                var action = context.Request.Path.Value!["/__fixture/pack-execution/".Length..];
                if (action is "maker" or "foreign") selection.Account = action;
                else if (action == "state") { await context.Response.WriteAsJsonAsync(await State(data)); return; }
                else { context.Response.StatusCode = 404; return; }
                context.Response.StatusCode = 204;
            }); next(app);
        };
    }
    private static async Task<object> State(Data data) {
        var row = await data.Rows(B.Conversions).Find(new BsonDocument()).SingleOrDefaultAsync(); var balance = await data.BalanceAsync();
        var destination = row is null ? null : await data.Rows("stockBalances").Find(new BsonDocument("_id", row["destinationAfter"]["_id"])).SingleAsync();
        var value = destination is null ? null : await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", destination["_id"])).SingleAsync();
        return new { conversionCount = await data.Rows(B.Conversions).CountDocumentsAsync(new BsonDocument()),
            movementCount = await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)),
            valueTransitionCount = await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument("operation", "convert")),
            conversionId = row?["_id"].AsInt64.ToString(CultureInfo.InvariantCulture), sourceOnHand = balance["onHand"].AsInt64.ToString(CultureInfo.InvariantCulture),
            sourceHeld = balance["held"].AsInt64.ToString(CultureInfo.InvariantCulture), destinationPackages = destination?["onHand"].AsInt64.ToString(CultureInfo.InvariantCulture),
            destinationValue = value?["remainingValue"].AsString, durableSha256 = Hash(await Durable(data)), originalSha256 = Hash(await Preserved(data)) };
    }
    private static async Task<BsonDocument> Durable(Data data) {
        var result = new BsonDocument();
        foreach (var table in new[] { B.Conversions, "stockBalances", "lots", "movements", LotValueStore.HeldHeads, LotValueStore.AvailableHeads, LotValueStore.Transitions })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument(field, B.Operation)).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static async Task<BsonDocument> Preserved(Data data) {
        var result = new BsonDocument { { "valuation", await data.Valuation.StateAsync() }, { "documents", await data.Valuation.Documents.StateAsync() },
            { "inspection", await data.Valuation.Inspection.StateAsync() }, { "receiving", await data.Valuation.Inspection.Receiving.StateAsync() }, { "orders", await data.Valuation.Inspection.Receiving.Orders.StateAsync() } };
        foreach (var table in new[] { "procurementStockReceipts", "procurementSupplierPayableProposals", "procurementSupplierPayableDecisions", "procurementSupplierPayableAllocations",
            "cashMovements", "paymentAllocations", "procurementFundingAllocations", "procurementDisbursements", "procurementSettlements", "reservations" })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static string Hash(BsonDocument row) => Convert.ToHexString(SHA256.HashData(row.ToBson())).ToLowerInvariant();
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the browser proof from the authoritative checkout.");
    }
}

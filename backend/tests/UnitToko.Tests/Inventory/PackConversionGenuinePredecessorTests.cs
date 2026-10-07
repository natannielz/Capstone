using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.PackConversionBson;
using K = UnitToko.Platform.Inventory.PackConversionRuleBson;
using R = UnitToko.Platform.Inventory.StockReleaseBson;

namespace UnitToko.Tests.Inventory;

/// <summary>Consumes an actual stopped, pinned103 producer. No current-code103
/// fixture, deleted future owner, reseeded approval or manufactured capitalization.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionGenuinePredecessorTests(MongoFixture fixture)
{
    internal const string HandoffEnvironment = "UNITTOKO_PACK_CONVERSION_PREDECESSOR103";
    private const string SourceCommit = "5695f59c7bb18afcaaf555b95128db070988d537";
    private const string ProgramPin = "C52E34F17C7A5C41F7961C4F6FB269633DDACF26CBF7833942E5AB4546BEFDA5";
    private const string HostPin = "C4F057326CBFEC002E5A9B6CE21025AB06DC7B7B37C208BCCBC7D81F90180F93";
    private const string RebindPin = "BC603758BE7AB1DFFF98D433973A6BEFD750E8D7723393AB33CB83009DCAD923";
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal) {
        ["UnitToko.Api"] = "C751FE59037CE4C46B39E6D604B13A57E0A8F48150BB2CA27ECA031F838CA844",
        ["UnitToko.Platform"] = "2DA2918E8C8E6B0D7EFC7F1ADD13F93084D0B86CA118FC00368B4D6BB965DF83",
        ["UnitToko.Modules"] = "7B437FCFB83E40B587AB1561CAC745BC520065816E2DADE2052E7B2134123EEC",
        ["MongoDB.Driver"] = "4B920940FD9C325772830F0180EC7B28DF099647E23B79D732EBBCF93E6FE1A1",
        ["MongoDB.Bson"] = "162F2E0F82195544D97A96765C9CB9DEAEDBFD9DD1600CE1B3D128DB9FCB4DC8",
        ["Microsoft.AspNetCore.Authentication.OpenIdConnect"] = "CDFC499638805AD451402CB58CBBB0678A345FE20834D38E64A144D75E293B9A",
        ["Microsoft.IdentityModel.Abstractions"] = "84A6C307FE84453C9FC3A1AF45BF2302B5D2ABA4D5B9BFB7D581D6A28787461F",
        ["Microsoft.IdentityModel.JsonWebTokens"] = "4F61FD3D3F5003951A09CDD27FFDC3C4841A158D27A6D7247BE3E5939E7DAB32",
        ["Microsoft.IdentityModel.Logging"] = "2BC4582AE62236F066901A7DCB2A88D04A181AD1978F2DB2F4314DF6AE1A50C6",
        ["Microsoft.IdentityModel.Protocols"] = "40ADA0B98384788970117CADF63863EDD99BD5BD6FB8076F6DA68D65F6DBDA4F",
        ["Microsoft.IdentityModel.Protocols.OpenIdConnect"] = "7921AEF6FA1DA2E563A237DDA129B12407AC6C56448BB0EB7EEBC59BB6C6BABB",
        ["Microsoft.IdentityModel.Tokens"] = "106DB045348A9B767F3002D4EC3BB582C9141F6D8E9966E1E0AD58A71BA42B8B",
        ["System.IdentityModel.Tokens.Jwt"] = "3D55B14EABADAF18AEFEA2C4EB085428E0485063AE3EA91CAC072E3B5F951A83" };
    private static string[] Acquisition => ["procurementStockReceipts", "procurementInspectionSubmissions", "supplierDocumentSubmissions", "supplierDocumentMatches",
        "procurementQualityAcceptanceProposals", "procurementQualityAcceptanceDecisions", "procurementQualityAcceptanceAllocations",
        "procurementStockValuationProposals", "procurementStockValuationDecisions", "procurementStockValuationAllocations",
        "procurementSupplierPayableProposals", "procurementSupplierPayableDecisions", "procurementSupplierPayableAllocations",
        "journalEntries", "journals", "journalLines", "cashMovements", "paymentAllocations", "procurementFundingAllocations",
        "procurementDisbursements", "procurementSettlements", "evidencePins", "evidencePinAudits",
        R.Proposals, R.Decisions, K.Proposals, K.Decisions, K.Rules, LotValueStore.HeldHeads];
    private static string[] Effects => [R.Proposals, R.Decisions, K.Proposals, K.Decisions, K.Rules, B.Conversions,
        LotValueStore.HeldHeads, LotValueStore.AvailableHeads, LotValueStore.Transitions, "lots", "stockBalances", "movements",
        "reservations", "auditEvents", "outboxEvents", "idempotencyRecords"];

    [WindowsStorageFact]
    public async Task Genuine_stopped103_release_and_approved_rule_upgrade104_preserves_original_bytes_and_keys_then_converts_once_with_conserved_stock_and_value()
    {
        var path = Environment.GetEnvironmentVariable(HandoffEnvironment);
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Genuine stopped103 released-value/approved-rule handoff is required; genuine103 migration acceptance is unavailable");
        var writer = Path.GetFullPath(Path.Combine(FindRoot(), "tmp/production/pack-conversion-genuine103-producer-release1"));
        path = Path.GetFullPath(path); RequireChild(writer, path); Assert.Equal("HANDOFF.json", Path.GetFileName(path));
        var directory = Path.GetDirectoryName(path)!;
        Assert.Equal(writer, Path.GetDirectoryName(directory)); Assert.Matches(@"\Acapsule-[a-z0-9-]{1,48}\z", Path.GetFileName(directory));
        using var readyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var ready = readyDocument.RootElement;
        using var stopDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-STOPPED.json"))); var stop = stopDocument.RootElement;
        using var observedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "PRODUCER-EXTERNAL-OBSERVATION.json"))); var observed = observedDocument.RootElement;
        Assert.Equal("GENUINE103_RELEASED_VALUE_AND_APPROVED_RULE_READY", Text(ready, "status")); Assert.Equal(SourceCommit, Text(ready, "authoritativeSourceCommit"));
        Assert.True(ready.GetProperty("syntheticLocalOnly").GetBoolean()); Assert.False(ready.GetProperty("productionAcceptance").GetBoolean());
        Assert.True(ready.GetProperty("producerWritesStopped").GetBoolean());
        var name = Text(ready, "databaseName"); Assert.Matches(@"\Aunittoko_test_[0-9a-f]{32}_[0-9a-f]{8}\z", name);
        foreach (var facts in new[] {ready, stop, observed}) {
            Assert.Equal(103, facts.GetProperty("schema").GetInt32()); Assert.Equal(name, Text(facts, "databaseName"));
            Assert.Equal("packConversions_ABSENT_NOT_REMOVED", Text(facts, "futureOwnersOrCounters"));
        }
        Assert.True(stop.GetProperty("producerWritesStopped").GetBoolean()); Assert.True(stop.GetProperty("databaseRetained").GetBoolean());
        Assert.False(stop.GetProperty("fixtureDisposed").GetBoolean()); Assert.Equal(0, stop.GetProperty("producerExitCode").GetInt32());
        Assert.True(observed.GetProperty("processStopped").GetBoolean()); Assert.Equal(0, observed.GetProperty("observedProducerExitCode").GetInt32());
        var pid = stop.GetProperty("processId").GetInt32(); Assert.True(pid > 0); Assert.Equal(pid, observed.GetProperty("processId").GetInt32());
        try { using var producer = Process.GetProcessById(pid); Assert.Fail("The exact genuine103 producer PID is still running"); }
        catch (ArgumentException) { }
        CheckFile(writer, path, Text(observed, "handoffSha256"));
        Assert.True(observed.GetProperty("exclusiveProducerLeaseObserved").GetBoolean());
        var leasePath = Path.GetFullPath(Path.Combine(directory, "producer103.one-writer.lease")); Assert.Equal(leasePath, Path.GetFullPath(Text(observed, "leasePath")));
        using var stoppedWriterLease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ValidateProducer(writer, ready, stop, observed, pid);

        var database = fixture.Client.GetDatabase(name);
        var clock = new MutableAuthorizationClock(DateTimeOffset.Parse(Text(ready, "businessClockUtc"), CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
        var before = await Snapshot(database); var history = before["schemaMigrations"]["rows"].AsBsonArray;
        Assert.Equal(103, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 103));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        ValidateCapturedSnapshot(ready, name, before);
        Assert.False(before.Contains(B.Conversions)); Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", B.Conversions)).AnyAsync());
        foreach (var table in new[] {"stockSourceActivations", "procurementStockReceipts", "procurementQualityAcceptanceAllocations",
            "procurementStockValuationAllocations", "journalEntries", "evidencePins", R.Proposals, R.Decisions, K.Rules, LotValueStore.AvailableHeads})
            Assert.NotEmpty(before[table]["rows"].AsBsonArray);
        var installedMovement = before["movements"]["definition"]["options"]["validator"].AsBsonDocument;
        Assert.Equal(2, installedMovement["$and"].AsBsonArray.Count);
        Assert.True(CatalogSchemas.SemanticEquals(installedMovement["$and"][1], InventoryMovementSchemas.LegacyValidator()), "Actual103 retains the exact legacy movement clause under its foundation guard");
        Assert.True(AccountContactStorage.SafeDefinition(before["movements"]["definition"].AsBsonDocument, InventoryMovementSchemas.LegacyValidator()));
        Assert.False(AccountContactStorage.SafeDefinition(before["movements"]["definition"].AsBsonDocument, InventoryMovementSchemas.Validator()));
        var expectedMovement = installedMovement.DeepClone().AsBsonDocument;
        expectedMovement["$and"][1] = InventoryMovementSchemas.Validator();

        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 104);
        Assert.True(await new MigrationGate(database, clock, 104).IsCurrentAsync(default));
        var after = await Snapshot(database); Assert.Equal(before.ElementCount + 1, after.ElementCount);
        foreach (var table in before.Names) {
            var expected = before[table].DeepClone().AsBsonDocument;
            if (table == "movements") expected["definition"]["options"]["validator"] = expectedMovement;
            if (table == "schemaMigrations") {
                Same(expected["definition"].AsBsonDocument, after[table]["definition"].AsBsonDocument, "history UUID/options");
                Same(new("indexes", expected["indexes"]), new("indexes", after[table]["indexes"]), "history indexes");
            } else Same(expected, after[table].AsBsonDocument, "genuine103 retained " + table);
        }
        Same(installedMovement["$and"][0].AsBsonDocument, after["movements"]["definition"]["options"]["validator"]["$and"][0].AsBsonDocument, "original103 foundation guard");
        var applied = after["schemaMigrations"]["rows"].AsBsonArray; Assert.Equal(104, applied.Count);
        Assert.True(MigrationHistory.IsValid(applied.Select(row => row.AsBsonDocument).ToArray(), 104));
        Same(new("rows", history), new("rows", new BsonArray(applied.Take(103))), "original103 migration bytes"); Assert.Equal("applied", applied[103]["state"]);
        await ConversionEmpty(database);
        await StoreTestData.ApplyRegisteredThroughAsync(database, clock, 104); Same(after, await Snapshot(database), "104 no-op migration replay");

        // Preservation is established before owning recoveries/replays advance
        // their legitimate coordination fences and allocator counters.
        var originalAcquisition = await CaptureRows(database, Acquisition);
        var originalEffects = await CaptureRows(database, Effects);
        var unit = new MongoUnitOfWork(fixture.Client); var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog); var supplier = new SupplierService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, supplier); var sources = new StockSourceSetupService(database, unit, ids, clock);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var valuation = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, sources);
        StockReleaseService Releases() => new(database, unit, ids, clock, catalog, orders, sources, valuation);
        PackConversionService Conversions() => new(database, unit, ids, clock, catalog, orders, sources, valuation, Releases());
        ActorContext Actor(string field) => JsonSerializer.Deserialize<ActorContext>(ready.GetProperty("actors").GetProperty(field).GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Original synthetic actor descriptor is required");
        var releaseProposal = await Original(database, R.Proposals, Actor("releaseMaker"), Text(ready, "releaseProposeOriginalKey"));
        var releaseDecision = await Original(database, R.Decisions, Actor("releaseChecker"), Text(ready, "releaseDecisionOriginalKey"));
        var ruleProposal = await Original(database, K.Proposals, Actor("ruleMaker"), Text(ready, "ruleProposeOriginalKey"));
        var ruleDecision = await Original(database, K.Decisions, Actor("ruleChecker"), Text(ready, "ruleDecisionOriginalKey"));
        var releasedInput = R.Input(releaseProposal["input"]); var ruleInput = K.Input(ruleProposal["input"].AsBsonDocument);
        Assert.Equal(Number(ready, "receiptId"), releasedInput.ReceiptId); Assert.Equal(Number(ready, "qualityAllocationId"), releasedInput.QualityAllocationId);
        Assert.Equal(Number(ready, "valuationAllocationId"), releasedInput.ValuationAllocationId);
        Assert.Equal(Number(ready, "ruleId"), ruleDecision["ruleId"].AsInt64);
        Assert.Equal(Number(ready, "sourceSkuId"), ruleInput.SourceSkuId); Assert.Equal(Number(ready, "sourceDefinitionId"), ruleInput.SourceDefinitionId);
        Assert.Equal(Number(ready, "destinationSkuId"), ruleInput.DestinationSkuId); Assert.Equal(Number(ready, "destinationDefinitionId"), ruleInput.DestinationDefinitionId);
        var releaseProposalResult = CapturedResult(ready, "releaseProposalResultBsonBase64"); var releaseDecisionResult = CapturedResult(ready, "releaseDecisionResultBsonBase64");
        var ruleProposalResult = CapturedResult(ready, "ruleProposalResultBsonBase64"); var ruleDecisionResult = CapturedResult(ready, "ruleDecisionResultBsonBase64");
        Same(releaseProposalResult, R.Result(releaseProposal), "original release proposal result"); Same(releaseDecisionResult, R.Result(releaseDecision), "original release approval result");
        Same(ruleProposalResult, K.Result(ruleProposal), "original rule proposal result"); Same(ruleDecisionResult, K.Result(ruleDecision), "original rule approval result");
        var releaseService = Releases();
        Assert.Equal(releaseProposalResult, await releaseService.ProposeAsync(Actor("releaseMaker"), releasedInput,
            R.Expected(releaseProposal["expected"]), releaseProposal["binding"].AsString, Metadata(releaseProposal)));
        Assert.Equal(releaseDecisionResult, await releaseService.DecideAsync(Actor("releaseChecker"), releaseDecision["proposalId"].AsInt64, 1,
            StockReleaseDecision.Approve, releasedInput.Policy, R.Expected(releaseDecision["expected"]), releaseDecision["binding"].AsString, Metadata(releaseDecision)));
        Assert.Equal(releaseProposalResult, await Releases().RecoverAsync(Actor("releaseMaker"), R.Propose, Metadata(releaseProposal).IdempotencyKey));
        Assert.Equal(releaseDecisionResult, await Releases().RecoverAsync(Actor("releaseChecker"), R.Decide, Metadata(releaseDecision).IdempotencyKey));
        var rules = new PackConversionRuleService(database, unit, ids, clock);
        Assert.Equal(ruleProposalResult, await rules.ProposeRuleAsync(Actor("ruleMaker"), ruleInput, ruleProposal["binding"].AsString, Metadata(ruleProposal)));
        Assert.Equal(ruleDecisionResult, await rules.DecideRuleAsync(Actor("ruleChecker"), ruleDecision["proposalId"].AsInt64, 1,
            PackConversionRuleDecision.Approve, ruleDecision["binding"].AsString, Metadata(ruleDecision)));
        var restartedRules = new PackConversionRuleService(database, unit, ids, clock);
        Assert.Equal(ruleProposalResult, await restartedRules.LookupAsync(Actor("ruleMaker"), K.Propose, Metadata(ruleProposal).IdempotencyKey));
        Assert.Equal(ruleDecisionResult, await restartedRules.LookupAsync(Actor("ruleChecker"), K.Decide, Metadata(ruleDecision).IdempotencyKey));
        Same(originalEffects, await CaptureRows(database, Effects), "old original-key replays produced no physical/value/receipt effects");
        Same(originalAcquisition, await CaptureRows(database, Acquisition), "old original-key replays preserved acquisition/accounting");
        await ConversionEmpty(database);

        var receipt = await Rows(database, "procurementStockReceipts").Find(new BsonDocument("_id", releasedInput.ReceiptId)).SingleAsync();
        var balanceId = receipt["balanceId"].AsInt64; var sourceBefore = await Rows(database, "stockBalances").Find(new BsonDocument("_id", balanceId)).SingleAsync();
        Assert.Equal(3L, sourceBefore["onHand"].AsInt64); Assert.Equal(2L, sourceBefore["held"].AsInt64); Assert.Equal(0L, sourceBefore["reserved"].AsInt64);
        var heldBefore = await Rows(database, LotValueStore.HeldHeads).Find(new BsonDocument("_id", releasedInput.ValuationAllocationId)).SingleAsync();
        var valueBefore = await Rows(database, LotValueStore.AvailableHeads).Find(new BsonDocument("_id", balanceId)).SingleAsync();
        Assert.Equal(1L, heldBefore["remainingPackages"].AsInt64); Assert.Equal("11250.00", heldBefore["remainingValue"].AsString);
        Assert.Equal(1L, valueBefore["remainingPackages"].AsInt64); Assert.Equal("11250.00", valueBefore["remainingValue"].AsString);
        var conversionInput = new PackConversionInput(ruleInput.Location, Number(ready, "ruleId"), 1, balanceId, 1);
        var maker = Actor("ruleMaker"); var service = Conversions(); var preview = await service.PreviewAsync(maker, conversionInput);
        Assert.Equal(1L, preview["quantities"]["sourcePackages"].AsInt64); Assert.Equal(12L, preview["quantities"]["destinationPackages"].AsInt64);
        Assert.Equal(12L, preview["quantities"]["baseUnits"].AsInt64); Assert.Equal(valueBefore["remainingValue"], preview["sourceValue"]["remainingValue"]);
        Same(originalEffects, await CaptureRows(database, Effects), "conversion preview produced no business effects");
        var expectedConversion = B.Expected(preview["expected"]); var binding = preview["binding"].AsString;
        var metadata = new AuthorizationCommandMetadata("genuine103-to104-conversion-original", "Synthetic genuine103 sealed to loose conversion", "genuine103-to104-conversion");
        var result = await service.ExecuteAsync(maker, conversionInput, expectedConversion, binding, metadata);
        Assert.Equal("converted", result["status"].AsString); Assert.Equal("11250.00", result["transferredValue"].AsString);
        Assert.Equal(1L, result["sourcePackages"].AsInt64); Assert.Equal(12L, result["destinationPackages"].AsInt64); Assert.Equal(12L, result["baseUnits"].AsInt64);
        var sourceAfter = await Rows(database, "stockBalances").Find(new BsonDocument("_id", balanceId)).SingleAsync();
        var destination = await Rows(database, "stockBalances").Find(new BsonDocument("_id", result["destinationBalanceId"])).SingleAsync();
        Assert.Equal(2L, sourceAfter["onHand"].AsInt64); Assert.Equal(2L, sourceAfter["held"].AsInt64);
        Assert.Equal(0L, sourceAfter["reserved"].AsInt64); Assert.Equal(0L, sourceAfter["expired"].AsInt64);
        Assert.Equal(12L, destination["onHand"].AsInt64); Assert.Equal(0L, destination["held"].AsInt64);
        Assert.Equal(0L, destination["reserved"].AsInt64); Assert.Equal(0L, destination["expired"].AsInt64);
        Assert.Equal(checked(sourceBefore["onHand"].AsInt64 * 12), checked(sourceAfter["onHand"].AsInt64 * 12 + destination["onHand"].AsInt64));
        var sourceValue = await Rows(database, LotValueStore.AvailableHeads).Find(new BsonDocument("_id", balanceId)).SingleAsync();
        var destinationValue = await Rows(database, LotValueStore.AvailableHeads).Find(new BsonDocument("_id", destination["_id"])).SingleAsync();
        Assert.Equal(0L, sourceValue["remainingPackages"].AsInt64); Assert.Equal("0.00", sourceValue["remainingValue"].AsString);
        Assert.Equal(12L, destinationValue["remainingPackages"].AsInt64); Assert.Equal("11250.00", destinationValue["remainingValue"].AsString);
        Same(heldBefore, await Rows(database, LotValueStore.HeldHeads).Find(new BsonDocument("_id", releasedInput.ValuationAllocationId)).SingleAsync(), "held remainder is conserved");
        Assert.Equal(Money(heldBefore) + Money(valueBefore), Money(heldBefore) + Money(sourceValue) + Money(destinationValue));
        var conversion = Assert.Single(await Rows(database, B.Conversions).Find(new BsonDocument()).ToListAsync());
        Assert.Equal("committed", conversion["phase"].AsString); Assert.Equal(metadata.IdempotencyKey, conversion["requestKey"]);
        Assert.Equal(maker.UserId, conversion["actorId"].AsInt64); Assert.Equal(Number(ready, "ruleId"), conversion["input"]["ruleId"].AsInt64);
        Same(B.Result(conversion), result, "conversion original result");
        var destinationLot = await Rows(database, "lots").Find(new BsonDocument("_id", result["destinationLotId"])).SingleAsync();
        Assert.Equal("packageConversion", destinationLot["origin"]); Assert.Equal(conversion["_id"], destinationLot["sourceId"]);
        Assert.Equal(conversion["sourceLot"]["_id"], destinationLot["parentLotId"]); Assert.Equal(conversion["sourceLot"]["rootLotId"], destinationLot["rootLotId"]);
        Assert.Equal(conversion["sourceLot"]["expiresOn"], destinationLot["expiresOn"]);
        Assert.Equal(Number(ready, "destinationSkuId"), destinationLot["skuId"].AsInt64); Assert.Equal(Number(ready, "destinationDefinitionId"), destinationLot["definitionId"].AsInt64);
        Assert.Equal(1L, await Rows(database, "movements").CountDocumentsAsync(new BsonDocument("source.namespace", B.Namespace)));
        Assert.Equal(1L, await Rows(database, LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument("operation", "convert")));
        foreach (var table in new[] {"lots", "movements", LotValueStore.Transitions}) {
            Assert.Equal(originalEffects[table].AsBsonArray.Count + 1, (await RowArray(database, table)).Count);
            foreach (var row in originalEffects[table].AsBsonArray) Same(row.AsBsonDocument,
                await Rows(database, table).Find(new BsonDocument("_id", row["_id"])).SingleAsync(), "original103 " + table + " row");
        }
        Same(originalAcquisition, await CaptureRows(database, Acquisition), "conversion preserves original approvals, acquisition, payable and journals; no COGS");
        foreach (var (table, field) in new[] {("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation")})
            Assert.Equal(1L, await Rows(database, table).CountDocumentsAsync(new BsonDocument(field, B.Operation)));
        var committedEffects = await CaptureRows(database, Effects); var restarted = Conversions();
        Assert.Equal(result, await restarted.ExecuteAsync(maker, conversionInput, expectedConversion, binding, metadata));
        Assert.Equal(result, await Conversions().RecoverAsync(maker, metadata.IdempotencyKey));
        Same(committedEffects, await CaptureRows(database, Effects), "fresh-service original-key conversion replay/recovery has exactly one effect");
        Same(originalAcquisition, await CaptureRows(database, Acquisition), "replay/recovery preserves acquisition and accounting");
        Same(new("rows", applied), new("rows", await RowArray(database, "schemaMigrations")), "consumer retains104 history");
    }

    private static void ValidateProducer(string writer, JsonElement ready, JsonElement stop, JsonElement observed, int pid)
    {
        foreach (var (pathField, hashField) in new[] {("executedProgramPath", "executedProgramSha256"), ("executedHostPath", "executedHostSha256"),
            ("retainedSnapshotPath", "retainedSnapshotSha256"), ("preservedBindingPath", "preservedBindingSha256"), ("preparationManifestPath", "preparationManifestSha256")}) {
            CheckFile(writer, Text(ready, pathField), Text(ready, hashField));
            if (stop.TryGetProperty(hashField, out var stopHash)) Assert.Equal(Text(ready, hashField), stopHash.GetString());
            if (observed.TryGetProperty(hashField, out var observedHash)) Assert.Equal(Text(ready, hashField), observedHash.GetString());
            if (observed.TryGetProperty(pathField, out var observedPath)) Assert.Equal(Path.GetFullPath(Text(ready, pathField)), Path.GetFullPath(observedPath.GetString()!));
        }
        Assert.Equal(ProgramPin, Text(ready, "executedProgramSha256")); Assert.Equal(HostPin, Text(ready, "executedHostSha256"));
        CheckFile(writer, Text(observed, "preparedHostRebindPath"), RebindPin); Assert.Equal(RebindPin, Text(observed, "preparedHostRebindSha256"));
        using var rebindDocument = JsonDocument.Parse(File.ReadAllText(Text(observed, "preparedHostRebindPath"))); var rebind = rebindDocument.RootElement;
        Assert.Equal(103, rebind.GetProperty("schema").GetInt32()); Assert.Equal(104, rebind.GetProperty("targetConsumerSchema").GetInt32());
        Assert.Equal(SourceCommit, Text(rebind, "authoritativeSourceCommit")); Assert.Equal(ProgramPin, Text(rebind, "programSha256")); Assert.Equal(HostPin, Text(rebind, "actualFrozenHostSha256"));
        Assert.True(rebind.GetProperty("directFrozenProductReferencesOnly").GetBoolean()); Assert.False(rebind.GetProperty("productionSourceCompiled").GetBoolean());
        Assert.False(rebind.GetProperty("sourceProjectOrProductPinsChanged").GetBoolean()); Assert.False(rebind.GetProperty("originalBinaryEqualityClaimed").GetBoolean());
        Assert.Equal(Text(ready, "preservedBindingSha256"), Text(rebind, "runtimeBindingSha256"));
        Assert.Equal(Text(ready, "preparationManifestSha256"), Text(rebind, "preparationManifestSha256"));
        CheckFile(writer, Path.Combine(writer, "run-native-producer.ps1"), Text(rebind, "launcherSha256"));
        CheckFile(writer, Path.Combine(writer, "Producer103.csproj"), Text(rebind, "projectSha256"));
        RequireChild(writer, Text(observed, "actualExitObservationPath"));
        using var exitDocument = JsonDocument.Parse(File.ReadAllText(Text(observed, "actualExitObservationPath"))); var exit = exitDocument.RootElement;
        Assert.Equal(pid, exit.GetProperty("processId").GetInt32());
        Assert.Equal(0, exit.GetProperty("observedProducerExitCode").GetInt32()); Assert.True(exit.GetProperty("processStopped").GetBoolean());
        foreach (var field in new[] {"executedProgramSha256", "executedHostSha256", "preparedHostRebindSha256"}) Assert.Equal(Text(observed, field), Text(exit, field));
        using var bindingDocument = JsonDocument.Parse(File.ReadAllText(Text(ready, "preservedBindingPath"))); var binding = bindingDocument.RootElement;
        Assert.Equal(103, binding.GetProperty("schema").GetInt32()); Assert.Equal(SourceCommit, Text(binding, "authoritativeSourceCommit"));
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in new[] {"productPins", "sharedPins"}) foreach (var pin in binding.GetProperty(field).EnumerateObject()) declared.Add(pin.Name, pin.Value.GetString()!);
        foreach (var pin in binding.GetProperty("compileDependencyPins").EnumerateObject()) declared.Add(pin.Name, Text(pin.Value, "sha256"));
        Assert.Equal(Pins.Count, declared.Count); foreach (var (binary, hash) in Pins) Assert.Equal(hash, declared[binary]);
        var executed = ready.GetProperty("runtimeBinaries").EnumerateArray().ToArray(); Assert.Equal(Pins.Count, executed.Length);
        Assert.Equal(Pins.Count, executed.Select(binary => Text(binary, "name")).Distinct(StringComparer.Ordinal).Count());
        foreach (var binary in executed) {
            var binaryName = Text(binary, "name"); Assert.Equal(Pins[binaryName], Text(binary, "sha256")); CheckFile(writer, Text(binary, "path"), Pins[binaryName]);
            if (binding.GetProperty("compileDependencyPins").TryGetProperty(binaryName, out var dependency))
                Assert.Equal(Text(dependency, "assemblyVersion"), AssemblyName.GetAssemblyName(Text(binary, "path")).Version!.ToString());
        }
        using var preparationDocument = JsonDocument.Parse(File.ReadAllText(Text(ready, "preparationManifestPath"))); var preparation = preparationDocument.RootElement;
        Assert.Equal(103, preparation.GetProperty("schema").GetInt32()); Assert.Equal("PREPARED_NOT_EXECUTED", Text(preparation, "status"));
        foreach (var file in preparation.GetProperty("files").EnumerateArray()) {
            // Explicit launcher/source repairs preserve their original manifest
            // bytes in the same paths verified by the actual producer launcher.
            var relative = Text(file, "relativePath") switch {
                "Program.cs" => "obj/runtime-binding-failure-release2/Program.cs",
                "run-native-producer.ps1" => "obj/launcher-rebuild-failure-release1/run-native-producer.ps1",
                var unchanged => unchanged };
            CheckFile(writer, Path.Combine(writer, relative), Text(file, "sha256"));
        }
        var hostDirectory = Path.GetDirectoryName(Text(ready, "executedHostPath"))!;
        foreach (var file in binding.GetProperty("allFrozenFiles").EnumerateArray()) if (file.GetProperty("copiedToHost").GetBoolean()) {
            var path = Path.GetFullPath(Path.Combine(hostDirectory, Text(file, "relativePath"))); RequireChild(hostDirectory, path);
            CheckFile(writer, path, Text(file, "sha256"));
        }
    }
    private static void ValidateCapturedSnapshot(JsonElement ready, string name, BsonDocument actual)
    {
        using var capturedDocument = JsonDocument.Parse(File.ReadAllText(Text(ready, "retainedSnapshotPath"))); var captured = capturedDocument.RootElement;
        Assert.Equal(103, captured.GetProperty("schema").GetInt32()); Assert.Equal(name, Text(captured, "databaseName"));
        Assert.Equal(Text(ready, "businessClockUtc"), Text(captured, "businessClockUtc")); Assert.Equal(actual.ElementCount, captured.GetProperty("collections").GetArrayLength());
        Assert.Equal(actual.Names.Order(StringComparer.Ordinal), captured.GetProperty("collections").EnumerateArray().Select(collection => Text(collection, "name")).Order(StringComparer.Ordinal));
        foreach (var collection in captured.GetProperty("collections").EnumerateArray()) {
            var table = Text(collection, "name"); Assert.True(actual.Contains(table));
            foreach (var part in new[] {"definition", "indexes", "rows"}) {
                var bytes = Convert.FromBase64String(Text(collection, part + "BsonBase64")); Assert.Equal(Text(collection, part + "Sha256"), Hash(bytes));
                Same(BsonSerializer.Deserialize<BsonDocument>(bytes), part == "definition" ? actual[table][part].AsBsonDocument : new(part, actual[table][part]), "genuine103 " + table + " " + part);
            }
        }
    }
    private static async Task ConversionEmpty(IMongoDatabase database)
    {
        await PackConversionSchemas.RequireStorageAsync(database, default); Assert.Empty(await RowArray(database, B.Conversions));
        Assert.False(await Rows(database, "idCounters").Find(new BsonDocument("_id", B.Conversions)).AnyAsync());
    }
    private static Task<BsonDocument> Original(IMongoDatabase database, string table, ActorContext actor, string key) =>
        Rows(database, table).Find(new BsonDocument {{"actorId", actor.UserId}, {"requestKey", key}}).SingleAsync();
    private static AuthorizationCommandMetadata Metadata(BsonDocument row) => new(row["requestKey"].AsString, row["reason"].AsString, row["correlationId"].AsString);
    private static BsonDocument CapturedResult(JsonElement ready, string field) => BsonSerializer.Deserialize<BsonDocument>(Convert.FromBase64String(Text(ready, field)));
    private static long Number(JsonElement row, string field) => long.Parse(Text(row, field), CultureInfo.InvariantCulture);
    private static decimal Money(BsonDocument row) => decimal.Parse(row["remainingValue"].AsString, CultureInfo.InvariantCulture);
    private static async Task<BsonDocument> CaptureRows(IMongoDatabase database, IEnumerable<string> tables)
    {
        var result = new BsonDocument(); foreach (var table in tables) result[table] = await RowArray(database, table); return result;
    }
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase database, string table) => database.GetCollection<BsonDocument>(table);
    private static async Task<BsonArray> RowArray(IMongoDatabase database, string table) => new(await Rows(database, table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> Snapshot(IMongoDatabase database)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await database.ListCollectionsAsync()).ToListAsync()).Where(row => row.GetValue("type", "") == "collection").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var table = definition["name"].AsString;
            result[table] = new BsonDocument {{"definition", definition}, {"indexes", new BsonArray((await (await Rows(database, table).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal))}, {"rows", await RowArray(database, table)}};
        }
        return result;
    }
    private static string Text(JsonElement row, string field) => row.GetProperty(field).GetString() ?? throw new InvalidOperationException("Required capsule field missing");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void RequireChild(string owner, string path) => Assert.StartsWith(Path.GetFullPath(owner).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    private static void CheckFile(string owner, string path, string expected) { RequireChild(owner, path); Assert.Equal(expected, Hash(File.ReadAllBytes(path))); }
    private static void Same(BsonDocument expected, BsonDocument actual, string label) => Assert.True(expected.ToBson().SequenceEqual(actual.ToBson()), label + " changed");
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend/UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout is required");
    }
}

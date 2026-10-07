using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using A = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal async Task<BsonDocument> ReadPackConversionSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope requested, long poolId, long expectedRevision, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); await SuspensionStorageAsync(ct);
        var context = await SuspensionContextAsync(actor, authority, tx, poolId, Permissions.StockConvert, ct);
        if (context.Pool["revision"] != expectedRevision) throw PackConversionRuleBson.Changed();
        var target = StockCountLocation(requested); var configured = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        if (configured.Location != target && !context.Configuration["mappings"].AsBsonArray.Any(alias => alias["requestedLocation"] == StockSourceSetupBson.Location(target) &&
            alias["enabled"] == true && alias["poolRevision"] == expectedRevision)) throw new AuthorizationDeniedException("scope_denied");
        Require(actor, authority, Permissions.StockConvert, Resource(target));
        await CompleteAuthorityAsync(actor, authority, Permissions.StockConvert, context.Resources, tx, ct);
        return new() { { "poolId", poolId }, { "revision", expectedRevision }, { "active", context.Pool["active"] },
            { "configurationHash", StockSourceSuspensionBson.Hash(context.Configuration) } };
    }
    internal async Task<BsonDocument> ReadPackConversionExecutionSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope requested, long poolId, long expectedRevision, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); await SuspensionStorageAsync(ct);
        var context = await SuspensionContextAsync(actor, authority, tx, poolId, Permissions.StockConvert, ct);
        var target = StockCountLocation(requested); var configured = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        if (context.Pool["revision"] != expectedRevision || !context.Pool["active"].AsBoolean) throw PackConversionBson.Changed();
        if (configured.Location != target && !context.Configuration["mappings"].AsBsonArray.Any(alias => alias["requestedLocation"] == StockSourceSetupBson.Location(target) &&
            alias["enabled"] == true && alias["poolRevision"] == expectedRevision)) throw new AuthorizationDeniedException("scope_denied");
        Require(actor, authority, Permissions.StockConvert, Resource(target));
        await RequireActivatedPoolAsync(tx, context.Pool, ct);
        var activation = await ActivationHistoryAsync(tx, context.Current, ct) ?? throw A.Unavailable();
        await CompleteAuthorityAsync(actor, authority, Permissions.StockConvert, context.Resources, tx, ct);
        return new() { { "poolId", poolId }, { "revision", expectedRevision }, { "active", true },
            { "configurationHash", StockSourceSuspensionBson.Hash(context.Configuration) }, { "configuration", context.Configuration.DeepClone() },
            { "requestedLocation", StockSourceSetupBson.Location(target) }, { "suspensionRevision", context.Latest?["suspensionRevision"] ?? 0L },
            { "poolFence", context.Pool["fence"] }, { "activation", activation.DeepClone() }, { "capturedAt", Now().UtcDateTime } };
    }
    internal async Task RequirePackConversionSourceHistoryAsync(IClientSessionHandle tx, BsonDocument source, DateTimeOffset at, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); await SuspensionStorageAsync(ct);
        var names = new[] { "poolId", "revision", "active", "configurationHash", "configuration", "requestedLocation", "suspensionRevision", "poolFence", "activation", "capturedAt" };
        if (source.ElementCount != names.Length || names.Any(name => !source.Contains(name)) || !source["active"].IsBoolean || !source["active"].AsBoolean ||
            !source["poolId"].IsInt64 || source["poolId"].AsInt64 <= 0 || !source["revision"].IsInt64 || source["revision"].AsInt64 <= 0 ||
            !source["poolFence"].IsInt64 || source["poolFence"].AsInt64 < 0 || !source["suspensionRevision"].IsInt64 || source["suspensionRevision"].AsInt64 < 0 ||
            !source["capturedAt"].IsBsonDateTime || !source["configuration"].IsBsonDocument || !source["activation"].IsBsonDocument ||
            !source["configurationHash"].IsString || source["configurationHash"] != StockSourceSuspensionBson.Hash(source["configuration"].AsBsonDocument) ||
            at > Now() || StockSourceSetupBson.Time(source["capturedAt"]) > at) throw PackConversionBson.Broken();
        var context = await HeldConfigurationAsync(tx, source["poolId"].AsInt64, ct);
        var binding = new BsonDocument { { "poolId", source["poolId"] }, { "poolRevision", source["revision"] },
            { "suspensionRevision", source["suspensionRevision"] }, { "configuration", source["configuration"] }, { "proposedAt", source["capturedAt"] } };
        await RequireOperatingPolicyBindingAsync(tx, binding, context, ct);
        await RequireOperatingPolicyBindingAtAsync(tx, binding, at, ct);
        var configuration = source["configuration"].AsBsonDocument;
        var target = InventoryGraph.Location(source["requestedLocation"].AsBsonDocument);
        var configured = StockSourceSetupBson.ReadInput(configuration["source"]["input"].AsBsonDocument);
        if (configuration["source"]["active"] != true || configured.Mode != StockAuthorityMode.WebLedger ||
            configured.Location != target && !configuration["mappings"].AsBsonArray.Any(alias => alias["requestedLocation"] == source["requestedLocation"] &&
                alias["enabled"] == true && alias["poolRevision"] == source["revision"])) throw PackConversionBson.Broken();
        _ = await ActivationHistoryAsync(tx, context.Current, ct);
        var stored = source["activation"].AsBsonDocument;
        if (!stored.GetValue("_id", BsonNull.Value).IsInt64 || stored["_id"].AsInt64 <= 0 ||
            !stored.GetValue("proposalId", BsonNull.Value).IsInt64 || !stored.GetValue("decisionId", BsonNull.Value).IsInt64) throw PackConversionBson.Broken();
        var actual = await HistoryOneAsync(tx, A.Activations, F.Eq("_id", stored["_id"]), ct) ?? throw A.Unavailable();
        var proposal = await ActivationProposalAsync(tx, stored["proposalId"].AsInt64, ct);
        var decision = await ActivationDecisionAsync(tx, proposal, ct) ?? throw A.Unavailable();
        if (actual != stored || decision["decision"] != "approve" || stored["decisionId"] != decision["_id"] ||
            stored["proposalHash"] != proposal["snapshotHash"] || stored["decisionHash"] != decision["snapshotHash"] || stored["poolId"] != source["poolId"] ||
            StockSourceSetupBson.Time(stored["recordedAt"]) > StockSourceSetupBson.Time(source["capturedAt"]) ||
            source["poolFence"].AsInt64 < stored["committedPoolFence"].AsInt64) throw PackConversionBson.Broken();
        var expected = A.ReadExpected(stored["expected"].AsBsonDocument);
        var reviewed = configuration.DeepClone().AsBsonDocument; reviewed["source"]["active"] = false;
        if (expected.PoolRevision != source["revision"].AsInt64 || expected.SuspensionRevision != source["suspensionRevision"].AsInt64 ||
            expected.ConfigurationHash != A.Hash(reviewed)) throw PackConversionBson.Broken();
    }
}

using UnitToko.Platform.Authorization;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceSetupBson
{
    internal static string Kind(string collection) => collection switch
    {
        "stockSourceProposals" => "stock.source.proposal",
        "stockSourceDecisions" => "stock.source.decision",
        "inventoryPoolRegistrations" => "inventory.pool.registration",
        "inventoryPools" => "inventory.pool",
        _ => throw Invalid()
    };
    internal static BsonDocument Header(string collection, long id) => new()
        { { "_id", id }, { "schemaVersion", 1 }, { "kind", Kind(collection) } };
    internal static BsonDocument Location(StockLocation location) => new()
    {
        { "organizationId", location.OrganizationId.Value }, { "shopId", location.ShopId.Value },
        { "warehouseId", location.WarehouseId.Value }, { "channelId", location.ChannelId.Value }
    };
    internal static BsonDocument Input(StockSourceSetupInput input) => new()
    {
        { "location", Location(input.Location) }, { "physicalKey", input.PhysicalKey }, { "label", input.Label },
        { "mode", input.Mode.ToString() }, { "control", input.Control.ToString() },
        { "maximumAgeMilliseconds", input.MaximumAgeMilliseconds }, { "safetyBufferBaseUnits", input.SafetyBufferBaseUnits },
        { "externalNamespace", (BsonValue?)input.ExternalNamespace ?? BsonNull.Value },
        { "reservationProtocolReference", (BsonValue?)input.ReservationProtocolReference ?? BsonNull.Value },
        { "discrepancyOwnerReference", input.DiscrepancyOwnerReference }, { "handoffReference", input.HandoffReference }
    };
    internal static StockSourceSetupInput ReadInput(BsonDocument row)
    {
        try
        {
            var input = new StockSourceSetupInput(InventoryGraph.Location(row["location"].AsBsonDocument),
                row["physicalKey"].AsString, row["label"].AsString,
                Enum.Parse<StockAuthorityMode>(row["mode"].AsString), Enum.Parse<StockPoolControl>(row["control"].AsString),
                row["maximumAgeMilliseconds"].AsInt64, row["safetyBufferBaseUnits"].AsInt64,
                row["externalNamespace"].IsBsonNull ? null : row["externalNamespace"].AsString,
                row["reservationProtocolReference"].IsBsonNull ? null : row["reservationProtocolReference"].AsString,
                row["discrepancyOwnerReference"].AsString, row["handoffReference"].AsString);
            StockSourceSetupPolicy.Validate(input);
            if (!Input(input).Equals(row)) throw Invalid();
            return input;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static void RequireProposal(BsonDocument row)
    {
        try
        {
            var input = ReadInput(row["input"].AsBsonDocument);
            if (row.ElementCount != 10 || !row["_id"].IsInt64 || row["_id"].AsInt64 <= 0 ||
                !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 || row["kind"] != Kind("stockSourceProposals") ||
                !row["revision"].IsInt64 || row["revision"].AsInt64 != 1 ||
                !row["proposedBy"].IsInt64 || row["proposedBy"].AsInt64 <= 0 ||
                !row["auditId"].IsInt64 || row["auditId"].AsInt64 <= 0 ||
                !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) || !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128))
                throw Invalid();
            _ = Time(row["proposedAt"]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static void RequireDecision(BsonDocument row, BsonDocument proposal)
    {
        try
        {
            if (row.ElementCount != 13 || row["schemaVersion"] != new BsonInt32(1) ||
                row["kind"] != Kind("stockSourceDecisions") || !row["_id"].IsInt64 || row["_id"].AsInt64 <= 0 ||
                !row["proposalId"].IsInt64 || row["proposalId"] != proposal["_id"] ||
                !row["revision"].IsInt64 || row["revision"] != proposal["revision"] ||
                !row["decidedBy"].IsInt64 || row["decidedBy"].AsInt64 <= 0 ||
                !row["auditId"].IsInt64 || row["auditId"].AsInt64 <= 0 ||
                !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) ||
                !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128) ||
                !StockSourceSetupPolicy.Text(row["reviewReference"].AsString, 160) ||
                Time(row["decidedAt"]) < Time(proposal["proposedAt"])) throw Invalid();
            var decision = row["decision"].AsString;
            if (decision is not ("approve" or "reject" or "withdraw") ||
                (decision == "withdraw") != (row["decidedBy"] == proposal["proposedBy"]) ||
                (decision == "approve" ? !row["poolId"].IsInt64 || row["poolId"].AsInt64 <= 0 : !row["poolId"].IsBsonNull))
                throw Invalid();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static DateTimeOffset Time(BsonValue value)
    {
        if (!value.IsBsonDateTime || value.AsBsonDateTime.MillisecondsSinceEpoch <= -62135596800000L ||
            value.AsBsonDateTime.MillisecondsSinceEpoch > 253402300799999L) throw Invalid();
        return new(value.ToUniversalTime(), TimeSpan.Zero);
    }
    internal static void Metadata(BsonDocument row, long auditId, AuthorizationCommandMetadata metadata)
    {
        row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId);
    }
    internal static BsonDocument Audit(BsonDocument row, ActorContext actor, string operation, DateTimeOffset now) => new()
    {
        { "_id", row["auditId"].AsInt64 }, { "schemaVersion", 1 }, { "kind", "stock.source.audit" },
        { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "createdAt", now.UtcDateTime }, { "record", row.DeepClone() }
    };
    internal static DomainValidationException Invalid() => new("stock_source_setup_unavailable", "Stock source setup records are not usable");
}

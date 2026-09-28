using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Inventory;

internal static class InventoryMovementBson
{
    internal static BsonDocument Create(long id, ActorContext actor, InventorySourceSnapshot snapshot,
        ImmutableArray<InventoryBalanceDocument> before, ImmutableArray<InventoryBalanceDocument> after,
        InventoryWriteMetadata metadata, DateTimeOffset recordedAt)
    {
        if (id <= 0 || before.IsDefaultOrEmpty || before.Length != snapshot.Changes.Length || after.Length != before.Length)
            throw InventoryLedger.Invalid("invalid_inventory_movement");
        var legs = new BsonArray();
        for (var index = 0; index < before.Length; index++)
        {
            var change = snapshot.Changes[index];
            if (before[index].Id != change.Id || after[index].Id != change.Id || before[index].PhysicalPoolId != snapshot.Pool.Id ||
                after[index].PhysicalPoolId != snapshot.Pool.Id || before[index].Balance.Key != change.Key || after[index].Balance.Key != change.Key ||
                before[index].Balance.Revision != change.ExpectedRevision || after[index].Balance.Revision != checked(change.ExpectedRevision + 1))
                throw InventoryLedger.Invalid("invalid_inventory_movement");
            legs.Add(new BsonDocument
            {
                { "balanceId", change.Id }, { "action", change.Action.ToString() }, { "packages", change.Packages },
                { "before", InventoryBson.Balance(before[index]) }, { "after", InventoryBson.Balance(after[index]) }
            });
        }
        return new()
        {
            { "_id", id }, { "schemaVersion", 1 }, { "kind", "inventory.movement" }, { "sourceEvent", snapshot.Source.EventKey },
            { "source", new BsonDocument { { "namespace", snapshot.Source.Namespace }, { "documentId", snapshot.Source.DocumentId },
                { "eventKind", snapshot.Source.EventKind }, { "occurrence", snapshot.Source.Occurrence } } },
            { "sourceRevision", snapshot.Revision }, { "physicalPoolId", snapshot.Pool.Id }, { "poolRevision", snapshot.Pool.Revision },
            { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "recordedAt", new BsonDateTime(recordedAt.UtcDateTime) },
            { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "legs", legs }
        };
    }
}

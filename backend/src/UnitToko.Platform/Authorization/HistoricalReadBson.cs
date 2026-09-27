using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using DomainQuery = UnitToko.Modules.Organizations.HistoricalReadQuery;

namespace UnitToko.Platform.Authorization;

internal static class HistoricalReadBson
{
    internal static BsonDocument Event(long id, ActorContext actor, HistoricalReadSource source, DomainQuery query,
        string operation, string correlationId, BsonDocument selection, long count, DateTimeOffset now) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "source", source.Key }, { "permission", query.Request.Permission }, { "purpose", query.Request.Purpose },
        { "operation", operation }, { "targetScope", AuthorizationBson.Scope(query.Request.TargetScope) },
        { "grantIds", new BsonArray(query.Clauses.Select(clause => clause.Grant.Id).Distinct().Order().Select(value => new BsonInt64(value))) },
        { "membershipIds", new BsonArray(query.Clauses.Select(clause => clause.Membership.Id).Distinct().Order().Select(value => new BsonInt64(value))) },
        { "selection", selection }, { "resultCount", count }, { "correlationId", correlationId }, { "createdAt", now.UtcDateTime }
    };

    internal static HistoricalMetadata Metadata(BsonDocument row) => new(row["_id"].AsInt64,
        row.TryGetValue("publicId", out var publicId) ? publicId.AsString : null,
        row.TryGetValue("displayName", out var displayName) ? displayName.AsString : null);
}

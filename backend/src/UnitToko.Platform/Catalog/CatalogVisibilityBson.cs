using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

internal static class CatalogVisibilityBson
{
    internal static BsonDocument Entitlement(CatalogEntitlement value) => new()
    {
        { "_id", value.Id }, { "schemaVersion", 1 }, { "userId", value.UserId }, { "membershipId", value.MembershipId },
        { "assortmentId", value.AssortmentId }, { "audienceScope", AuthorizationBson.Scope(value.AudienceScope) },
        { "status", AuthorizationBson.Name(value.Status) }, { "effectiveAt", value.EffectiveAt.UtcDateTime },
        { "expiresAt", At(value.ExpiresAt) }, { "revokedAt", At(value.RevokedAt) }, { "version", value.Version },
        { "requestedBy", value.RequestedBy }, { "approvedBy", value.ApprovedBy }, { "approvalId", value.ApprovalId },
        { "approvalVersion", value.ApprovalVersion }, { "approvedAt", value.ApprovedAt.UtcDateTime }, { "fence", 0L }
    };

    internal static BsonDocument Assortment(CatalogAssortment value) => new()
    {
        { "_id", value.Id }, { "schemaVersion", 1 }, { "ownerScope", AuthorizationBson.Scope(value.OwnerScope) },
        { "status", AuthorizationBson.Name(value.Status) }, { "version", value.Version }, { "fence", 0L }
    };

    internal static CatalogEntitlement Entitlement(BsonDocument value) => new(
        Id(value, "_id"), Id(value, "userId"), Id(value, "membershipId"), Id(value, "assortmentId"), Scope(value, "audienceScope"),
        Status(value), Date(value, "effectiveAt"), OptionalDate(value, "expiresAt"), OptionalDate(value, "revokedAt"),
        Id(value, "version"), Id(value, "requestedBy"), Id(value, "approvedBy"), Id(value, "approvalId"), Id(value, "approvalVersion"), Date(value, "approvedAt"));

    internal static CatalogAssortment Assortment(BsonDocument value) => new(Id(value, "_id"), Scope(value, "ownerScope"), Status(value), Id(value, "version"));
    private static long Id(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsInt64 && raw.AsInt64 > 0
        ? raw.AsInt64 : throw Invalid();
    private static GrantScope Scope(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsBsonDocument
        ? AuthorizationBson.Scope(raw.AsBsonDocument) : throw Invalid();
    private static AuthorizationStatus Status(BsonDocument value) => value.TryGetValue("status", out var raw) && raw.IsString &&
        Enum.TryParse<AuthorizationStatus>(raw.AsString, true, out var status) && Enum.IsDefined(status) ? status : throw Invalid();
    private static DateTimeOffset Date(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsBsonDateTime && raw.IsValidDateTime
        ? new(raw.ToUniversalTime(), TimeSpan.Zero) : throw Invalid();
    private static DateTimeOffset? OptionalDate(BsonDocument value, string field) => !value.TryGetValue(field, out var raw) || raw.IsBsonNull ? null : Date(value, field);
    private static BsonValue At(DateTimeOffset? at) => at.HasValue ? new BsonDateTime(at.Value.UtcDateTime) : BsonNull.Value;
    private static AuthorizationDeniedException Invalid() => new("invalid_catalog_authorization_data");
}

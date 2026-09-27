using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

internal static class AuthorizationBson
{
    public const int SchemaVersion = 1;
    public static string Name<T>(T value) where T : struct, Enum => char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
    private static T EnumValue<T>(BsonDocument document, string name) where T : struct, Enum =>
        document.TryGetValue(name, out var raw) && raw.IsString && Enum.TryParse<T>(raw.AsString, true, out var value) && Enum.IsDefined(value)
            ? value : throw new AuthorizationDeniedException("invalid_authorization_data");
    private static long Id(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsInt64 && raw.AsInt64 > 0
        ? raw.AsInt64 : throw new AuthorizationDeniedException("invalid_authorization_data");
    private static long? OptionalId(BsonDocument value, string field) => !value.TryGetValue(field, out var raw) || raw.IsBsonNull ? null : Id(value, field);
    private static DateTimeOffset At(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsValidDateTime
        ? new(raw.ToUniversalTime(), TimeSpan.Zero) : throw new AuthorizationDeniedException("invalid_authorization_data");
    private static DateTimeOffset? OptionalAt(BsonDocument value, string field) => !value.TryGetValue(field, out var raw) || raw.IsBsonNull ? null : At(value, field);
    private static string Text(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsString && !string.IsNullOrWhiteSpace(raw.AsString)
        ? raw.AsString : throw new AuthorizationDeniedException("invalid_authorization_data");
    private static ImmutableArray<string> Strings(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsBsonArray && raw.AsBsonArray.All(item => item.IsString)
        ? raw.AsBsonArray.Select(item => item.AsString).ToImmutableArray() : [];
    private static void Optional(BsonDocument document, string key, long? value) { if (value.HasValue) document.Add(key, value.Value); }
    private static void Optional(BsonDocument document, string key, DateTimeOffset? value) { if (value.HasValue) document.Add(key, value.Value.UtcDateTime); }

    public static BsonDocument Scope(GrantScope scope)
    {
        var value = new BsonDocument("kind", Name(scope.Kind));
        Optional(value, "organizationId", scope.OrganizationId); Optional(value, "branchId", scope.BranchId);
        Optional(value, "divisionId", scope.DivisionId); Optional(value, "shopId", scope.ShopId);
        Optional(value, "warehouseId", scope.WarehouseId); Optional(value, "channelId", scope.ChannelId);
        return value;
    }
    public static GrantScope Scope(BsonDocument value) => new(EnumValue<GrantScopeKind>(value, "kind"),
        OptionalId(value, "organizationId"), OptionalId(value, "branchId"), OptionalId(value, "divisionId"),
        OptionalId(value, "shopId"), OptionalId(value, "warehouseId"), OptionalId(value, "channelId"));
    public static BsonDocument Node(ScopeNode node)
    {
        var value = new BsonDocument { { "_id", node.Id }, { "schemaVersion", SchemaVersion }, { "kind", Name(node.Kind) },
            { "status", Name(node.Status) }, { "version", node.Version }, { "fence", 0L } };
        Optional(value, "parentId", node.ParentId);
        return value;
    }
    public static ScopeNode Node(BsonDocument value) => new(Id(value, "_id"), EnumValue<ScopeNodeKind>(value, "kind"),
        OptionalId(value, "parentId"), EnumValue<AuthorizationStatus>(value, "status"), Id(value, "version"));
    public static BsonDocument Membership(Membership membership)
    {
        var value = new BsonDocument { { "_id", membership.Id }, { "schemaVersion", SchemaVersion }, { "userId", membership.UserId },
            { "scope", Scope(membership.Scope) }, { "status", Name(membership.Status) }, { "approvedBy", membership.ApprovedBy },
            { "effectiveAt", membership.EffectiveAt.UtcDateTime }, { "version", membership.Version } };
        Optional(value, "expiresAt", membership.ExpiresAt); Optional(value, "revokedAt", membership.RevokedAt);
        return value;
    }
    public static Membership Membership(BsonDocument value) => new(Id(value, "_id"), Id(value, "userId"), Scope(value["scope"].AsBsonDocument),
        EnumValue<AuthorizationStatus>(value, "status"), Id(value, "approvedBy"), At(value, "effectiveAt"),
        OptionalAt(value, "expiresAt"), OptionalAt(value, "revokedAt"), Id(value, "version"));
    public static BsonDocument Grant(PermissionGrant grant)
    {
        var value = new BsonDocument { { "_id", grant.Id }, { "schemaVersion", SchemaVersion }, { "userId", grant.UserId },
            { "membershipId", grant.MembershipId }, { "templateId", grant.TemplateId }, { "templateVersion", grant.TemplateVersion },
            { "permissions", new BsonArray(grant.Permissions) }, { "scope", Scope(grant.Scope) }, { "status", Name(grant.Status) },
            { "requestedBy", grant.RequestedBy }, { "approvedBy", grant.ApprovedBy }, { "reason", grant.Reason },
            { "effectiveAt", grant.EffectiveAt.UtcDateTime }, { "version", grant.Version },
            { "delegablePermissions", new BsonArray(grant.DelegablePermissions.IsDefault ? [] : grant.DelegablePermissions) } };
        Optional(value, "expiresAt", grant.ExpiresAt); Optional(value, "revokedAt", grant.RevokedAt);
        return value;
    }
    public static PermissionGrant Grant(BsonDocument value) => Grant(value, false);
    public static PermissionGrant Grant(BsonDocument value, bool pending) => new(Id(value, "_id"), Id(value, "userId"), Id(value, "membershipId"),
        Text(value, "templateId"), value["templateVersion"].AsInt32, Strings(value, "permissions"), Scope(value["scope"].AsBsonDocument),
        EnumValue<AuthorizationStatus>(value, "status"), Id(value, "requestedBy"), pending && value["approvedBy"].IsInt64 && value["approvedBy"].AsInt64 == 0 ? 0 : Id(value, "approvedBy"), Text(value, "reason"),
        At(value, "effectiveAt"), OptionalAt(value, "expiresAt"), OptionalAt(value, "revokedAt"), Id(value, "version"), Strings(value, "delegablePermissions"));
    public static ResourceScope Resource(BsonDocument value)
    {
        ImmutableArray<long> assignedIds = [];
        if (value.TryGetValue("assignedUserIds", out var assigned))
        {
            if (!assigned.IsBsonArray) throw new AuthorizationDeniedException("invalid_authorization_data");
            assignedIds = assigned.AsBsonArray.Select(item => item.IsInt64 && item.AsInt64 > 0
                ? item.AsInt64 : throw new AuthorizationDeniedException("invalid_authorization_data")).ToImmutableArray();
        }
        var isAdministrative = false;
        if (value.TryGetValue("isAdministrative", out var administrative))
        {
            if (!administrative.IsBoolean) throw new AuthorizationDeniedException("invalid_authorization_data");
            isAdministrative = administrative.AsBoolean;
        }
        return new(OptionalId(value, "buyingOrganizationId"), OptionalId(value, "branchId"),
            OptionalId(value, "divisionId"), OptionalId(value, "fulfillmentOrganizationId"), OptionalId(value, "shopId"),
            OptionalId(value, "warehouseId"), OptionalId(value, "channelId"), OptionalId(value, "createdBy"), assignedIds, isAdministrative);
    }
}

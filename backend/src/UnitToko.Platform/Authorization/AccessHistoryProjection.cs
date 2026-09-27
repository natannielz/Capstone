using MongoDB.Bson;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    private static readonly HashSet<string> HistoricalScopeFields = new(StringComparer.Ordinal)
        { "kind", "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId" };
    private static readonly HashSet<string> OrganizationHistoryOperations = new(StringComparer.Ordinal)
        { "organization.proposed", "organization.approved", "organization.rejected", "organization.withdrawn" };
    private static readonly HashSet<string> AccessScopeHistoryOperations = new(StringComparer.Ordinal)
        { "access.grant.proposed", "access.grant.approved", "access.grant.revoked", "access.membership.proposed",
          "access.membership.approved", "access.membership.suspended", "access.proposal.rejected", "access.proposal.withdrawn" };

    // An immutable legacy snapshot is data, not an authorization grant. Unknown or malformed
    // scope stays unknown for the global auditor; scoped readers are filtered before paging.
    private static GrantScope? SafeHistoryScope(BsonDocument document, ScopeDirectory structuralDirectory)
    {
        if (document.TryGetValue("scope", out var explicitScope)) return ParseHistoricalScope(explicitScope, structuralDirectory);
        if (!AccessScopeHistoryOperations.Contains(document["operation"].AsString)) return null;
        foreach (var state in new[] { HistoricalDocument(document, "after"), HistoricalDocument(document, "before") })
            if (state.TryGetValue("scope", out var legacyScope)) return ParseHistoricalScope(legacyScope, structuralDirectory);
        return null;
    }

    private static GrantScope? ParseHistoricalScope(BsonValue value, ScopeDirectory structuralDirectory)
    {
        if (!value.IsBsonDocument) return null;
        var document = value.AsBsonDocument;
        if (document.Names.Any(name => !HistoricalScopeFields.Contains(name))) return null;
        try
        {
            var scope = AuthorizationBson.Scope(document);
            if (document["kind"] != AuthorizationBson.Name(scope.Kind)) return null;
            return ScopePolicy.IsScopeActive(scope, structuralDirectory) ? scope : null;
        }
        catch (AuthorizationDeniedException) { return null; }
    }

    private static (long? Id, long? BeforeVersion, long? AfterVersion, string? Status) HistoryResourceMetadata(BsonDocument document)
    {
        var before = HistoricalDocument(document, "before");
        var after = HistoricalDocument(document, "after");
        if (OrganizationHistoryOperations.Contains(document["operation"].AsString))
        {
            var afterScope = HistoricalDocument(after, "scope");
            if (HistoricalLong(afterScope, "_id") is not null)
            {
                before = HistoricalDocument(before, "scope"); after = afterScope;
            }
            else if (HistoricalLong(HistoricalDocument(after, "proposal"), "_id") is not null)
            {
                before = HistoricalDocument(before, "proposal"); after = HistoricalDocument(after, "proposal");
            }
        }
        var status = after.TryGetValue("status", out var rawStatus) && rawStatus.IsString &&
            (rawStatus.AsString is "pending" or "active" or "approved" or "rejected" or "withdrawn" or "revoked" or "suspended" ||
             document["operation"] == "identity.invitation.redeemed" && rawStatus.AsString == "redeemed")
            ? ProposalStatus(after) : null;
        return (HistoricalLong(after, "_id") ?? HistoricalLong(before, "_id"), HistoricalLong(before, "version"), HistoricalLong(after, "version"), status);
    }
    private static BsonDocument HistoricalDocument(BsonDocument parent, string field) =>
        parent.TryGetValue(field, out var value) && value.IsBsonDocument ? value.AsBsonDocument : new();
    private static long? HistoricalLong(BsonDocument document, string field)
    {
        var value = OptionalLong(document, field);
        return value is > 0 ? value : null;
    }
}

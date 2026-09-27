using System.Collections.Immutable;

namespace UnitToko.Modules.Organizations;

public enum ScopeNodeKind { BuyingOrganization, FulfillmentOrganization, Branch, Division, Shop, Warehouse, Channel }
public enum AuthorizationStatus { Pending, Active, Suspended, Revoked }

public sealed record ScopeNode(long Id, ScopeNodeKind Kind, long? ParentId, AuthorizationStatus Status, long Version);

public sealed record ScopeDirectory(ImmutableArray<ScopeNode> Nodes)
{
    public ScopeNode? Find(long id, ScopeNodeKind kind)
    {
        if (Nodes.IsDefault) return null;
        var matches = Nodes.Where(node => node.Id == id && node.Kind == kind).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool IsActive(long? id, ScopeNodeKind kind, long? parentId = null) =>
        id is > 0 && Find(id.Value, kind) is { Status: AuthorizationStatus.Active, Version: > 0 } node &&
        node.ParentId == parentId;
}

public enum GrantScopeKind
{
    BuyerOwned, BuyingDivision, BuyingBranch, BuyingOrganization,
    FulfillmentShop, FulfillmentWarehouse, CourierAssignment, System
}

/// <summary>Kind specifies breadth. A missing ID is never an implicit all-organizations scope.</summary>
public sealed record GrantScope(
    GrantScopeKind Kind, long? OrganizationId = null, long? BranchId = null,
    long? DivisionId = null, long? ShopId = null, long? WarehouseId = null, long? ChannelId = null);

/// <summary>Shared by detail, list, export, file and event policies.</summary>
public sealed record ResourceScope(
    long? BuyingOrganizationId = null, long? BranchId = null, long? DivisionId = null,
    long? FulfillmentOrganizationId = null, long? ShopId = null, long? WarehouseId = null,
    long? ChannelId = null, long? CreatedBy = null, ImmutableArray<long> AssignedUserIds = default,
    bool IsAdministrative = false);

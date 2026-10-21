using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

/// <summary>One grant of a persona: an existing role template, its finance packs and named additions, in one pilot scope.</summary>
public sealed record PersonaGrant(string TemplateId, string[] FinancePacks, string[] Additions, GrantScope Scope)
{
    public ImmutableArray<string> Permissions => RoleTemplates.Materialize(TemplateId, FinancePacks, Additions).Permissions;
}

/// <summary>A review persona. <c>tamu</c> is the signed-out visitor: no account, no grant, no session.</summary>
public sealed record PilotPersona(string Name, long AccountId, string Label, PersonaGrant[] Grants)
{
    public bool SignedIn => Grants.Length > 0;
}

/// <summary>
/// The people who use Unit Toko, as SYNTHETIC accounts of the pilot's own organisation (10, branches 11 and 12), shop (101),
/// warehouse (102) and channel (103). Every grant is <see cref="RoleTemplates.Materialize"/> of an existing template, so the
/// access screens show them as ordinary template grants. They join the full and review profiles; release1 keeps its four accounts.
/// </summary>
public static class PilotPersonas
{
    private static readonly GrantScope Branch11 = new(GrantScopeKind.BuyingBranch, 10, BranchId: 11);
    private static readonly GrantScope Branch12 = new(GrantScopeKind.BuyingBranch, 10, BranchId: 12);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static readonly GrantScope Warehouse = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    private static readonly GrantScope Courier = new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    private static readonly GrantScope System = new(GrantScopeKind.System);
    private static PersonaGrant Grant(string template, GrantScope scope, params string[] additions) => new(template, [], additions, scope);
    private static PersonaGrant Finance(string[] packs, GrantScope scope, params string[] additions) => new("finance", packs, additions, scope);

    public static readonly PilotPersona[] All = [
        new("tamu", 0, "Tamu (tanpa sesi)", []),
        new("pemohon", 3001, "Pemohon cabang A · SINTETIS", [Grant("buyer", Branch11, Permissions.BranchPic, Permissions.BranchOrderRequestRead,
            Permissions.BranchOrderRequestSubmit, Permissions.SubstitutionAccept, Permissions.OrganizationAddressRead)]),
        new("penyetuju-cabang", 3002, "Penyetuju belanja cabang A · SINTETIS", [Grant("buyer", Branch11, Permissions.BranchPic, Permissions.OrderSpendApprove,
            Permissions.OrderSpendingPolicyRead, Permissions.BranchOrderRequestRead, Permissions.OrganizationAddressRead)]),
        new("peninjau-toko", 3003, "Peninjau penerimaan toko · SINTETIS", [Grant("store-head", Shop, Permissions.OrderAccept, Permissions.BranchOrderRequestRead,
            Permissions.BranchOrderRequestDecide, Permissions.OrderCommercialPolicyRead, Permissions.AnomalyRead, Permissions.OrganizationAddressRead)]),
        // Catalog maker/checker: petugas-katalog proposes prices and the guest catalog; peninjau-katalog (no pricing.manage) approves them.
        new("petugas-katalog", 3004, "Petugas katalog · SINTETIS", [Grant("store-head", Shop, Permissions.CatalogPublicPropose)]),
        new("peninjau-katalog", 3005, "Peninjau harga dan katalog publik · SINTETIS", [Grant("store-staff", Shop, Permissions.PricingApprove,
            Permissions.CatalogPublicApprove)]),
        new("petugas-gudang", 3006, "Petugas gudang (penyiapan dan penerimaan) · SINTETIS", [Grant("store-staff", Warehouse, Permissions.StockReleasePropose,
            Permissions.StockConvert, Permissions.SubstitutionPropose, Permissions.ReservationRelease, Permissions.ProcurementContainerReceive, Permissions.ProcurementInspect,
            Permissions.ProcurementInspectionEvidenceRead, Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose,
            Permissions.StockSourceRead, Permissions.StockSourceReconciliationRead, Permissions.OrganizationAddressRead)]),
        // The dispatch page needs shipment.dispatch (store-staff) and shipment.assign together.
        new("petugas-kirim", 3007, "Petugas pengiriman · SINTETIS", [Grant("store-staff", Warehouse, Permissions.ShipmentAssign, Permissions.OrganizationAddressRead)]),
        new("kurir", 3008, "Kurir · SINTETIS", [Grant("courier", Courier)]),
        // Finance maker: invoice release proposal, invoice issue, payment recording/allocation, receiving accounts and stock valuation proposals.
        new("keuangan-tagihan", 3009, "Keuangan penagihan · SINTETIS", [Finance(["billing"], Shop, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleasePropose,
            Permissions.PaymentReviewRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountPropose, Permissions.ProcurementStockValuationRead,
            Permissions.ProcurementStockValuationPropose, Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead,
            Permissions.ProcurementRead, Permissions.StockRead, Permissions.CaseRead, Permissions.OrderRead, Permissions.OrganizationAddressRead)]),
        // Finance checker: payment verification, invoice release approval, bank sources, valuation, stock release and funding decisions.
        new("pemeriksa-pembayaran", 3010, "Pemeriksa pembayaran · SINTETIS", [Finance(["accounting"], Shop, Permissions.InvoiceRead, Permissions.PaymentReviewRead,
            Permissions.PaymentReviewCorrect, Permissions.PaymentReviewManual, Permissions.PaymentVerify, Permissions.BankReceiptRead, Permissions.BankReceiptConfirm,
            Permissions.BankStatementRead, Permissions.BankStatementStage, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleaseApprove,
            Permissions.ReceivingAccountRead, Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove, Permissions.ProcurementStockValuationRead,
            Permissions.ProcurementStockValuationDecide, Permissions.StockReleaseApprove, Permissions.FundingRead, Permissions.FundingApprove,
            Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.ProcurementSettlementRead,
            Permissions.ProcurementRead, Permissions.StockRead, Permissions.AccountingBookRead, Permissions.ReportRead, Permissions.OrganizationAddressRead)]),
        // Procurement maker/checker: pengadaan prepares (store-staff); penyetuju-pengadaan approves (store-head, no procurement.prepare).
        new("pengadaan", 3011, "Petugas pengadaan · SINTETIS", [Grant("store-staff", Shop, Permissions.SupplierChangePropose, Permissions.ProcurementSupplierDocumentRecord,
            Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankPropose,
            Permissions.SupplierClaim, Permissions.FundingRead, Permissions.ProcurementInspectionEvidenceRead, Permissions.ProcurementQualityAcceptanceRead,
            Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose, Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnPropose,
            Permissions.OrganizationAddressRead)]),
        new("penyetuju-pengadaan", 3012, "Penyetuju pengadaan · SINTETIS", [Grant("store-head", Shop, Permissions.SupplierChangeApprove,
            Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptanceDecide, Permissions.ProcurementSupplierDocumentEvidenceRead,
            Permissions.ProcurementInspectionEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankApprove, Permissions.SupplierClaimApprove,
            Permissions.FundingRead, Permissions.ProcurementPayableRead, Permissions.ProcurementPayableDecide, Permissions.ProcurementAdvanceReturnRead,
            Permissions.ProcurementAdvanceReturnDecide, Permissions.OrganizationAddressRead)]),
        // Access maker only: approvals belong to a second admin, so no access.approve / membership.approve here.
        new("admin-akses", 3013, "Admin akses · SINTETIS", [Grant("system-admin", System, Permissions.PersonReviewRead)]),
        new("staf-pembeli", 3014, "Staf toko yang juga memesan (cabang B) · SINTETIS", [Grant("store-staff", Shop), Grant("buyer", Branch12, Permissions.BranchPic)]),
    ];

    /// <summary>The four accounts every profile starts with; the bootstrap and its seeding actors depend on these ids.</summary>
    public static readonly long[] OriginalAccountIds = [1001, 1002, 2001, 2002];
    public static IEnumerable<PilotPersona> SignedIn => All.Where(persona => persona.SignedIn);
    public static IReadOnlyCollection<long> AccountIds => SignedIn.Select(persona => persona.AccountId).ToArray();

    /// <summary>The pilot bootstrap's synthetic organisation tree, as the authorization directory sees it.</summary>
    public static ScopeDirectory Directory { get; } = new([
        new(10, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1), new(11, ScopeNodeKind.Branch, 10, AuthorizationStatus.Active, 1),
        new(12, ScopeNodeKind.Branch, 10, AuthorizationStatus.Active, 1), new(100, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(101, ScopeNodeKind.Shop, 100, AuthorizationStatus.Active, 1), new(102, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1),
        new(103, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)]);

    /// <summary>One membership per distinct scope and one grant per template, requested by 2001 and approved by 2002 (both
    /// different people from every persona).</summary>
    public static (Membership[] Memberships, PermissionGrant[] Grants) Records(PilotPersona persona, IReadOnlyList<long> membershipIds,
        IReadOnlyList<long> grantIds, DateTimeOffset at)
    {
        var scopes = persona.Grants.Select(grant => grant.Scope).Distinct().ToArray();
        var memberships = scopes.Select((scope, index) => new Membership(membershipIds[index], persona.AccountId, scope, AuthorizationStatus.Active, 2002, at)).ToArray();
        var grants = persona.Grants.Select((grant, index) => new PermissionGrant(grantIds[index], persona.AccountId,
            memberships[Array.IndexOf(scopes, grant.Scope)].Id, grant.TemplateId, RoleTemplates.CurrentVersion, grant.Permissions, grant.Scope,
            AuthorizationStatus.Active, 2001, 2002, "SYNTHETIC review persona " + persona.Name + "; template grant, not a real assignment", at,
            DelegablePermissions: [])).ToArray();
        return (memberships, grants);
    }

    /// <summary>Adds each persona once (identity, profile, memberships, template grants, catalog entitlement for buying scopes and
    /// the synthetic person evidence the independent-person rules read). Ids come from the platform's counters.</summary>
    public static async Task SeedAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, CancellationToken ct)
    {
        var identities = database.GetCollection<BsonDocument>("identities");
        var ids = new IdAllocator(database);
        var unit = new MongoUnitOfWork(client);
        var added = new List<string>();
        foreach (var persona in SignedIn)
        {
            if (await identities.Find(new BsonDocument("_id", persona.AccountId)).AnyAsync(ct)) continue;
            var account = secrets.Accounts.SingleOrDefault(item => item.Id == persona.AccountId)
                ?? throw new InvalidOperationException("Pilot account manifest is missing persona " + persona.Name + ".");
            var scopes = persona.Grants.Select(grant => grant.Scope).Distinct().ToArray();
            var membershipIds = new List<long>(); foreach (var _ in scopes) membershipIds.Add((await ids.NextAsync("memberships", ct)).Value);
            var grantIds = new List<long>(); foreach (var _ in persona.Grants) grantIds.Add((await ids.NextAsync("authorizationGrants", ct)).Value);
            var buying = persona.Grants.Where(grant => grant.Scope.Kind == GrantScopeKind.BuyingBranch).ToArray();
            var entitlementIds = new List<long>(); foreach (var _ in buying) entitlementIds.Add((await ids.NextAsync("catalogEntitlements", ct)).Value);
            var at = DateTime.UtcNow.AddMinutes(-1);
            var (memberships, grants) = Records(persona, membershipIds, grantIds, new DateTimeOffset(at, TimeSpan.Zero));
            await unit.ExecuteAsync(async (tx, token) =>
            {
                async Task Insert(string name, params BsonDocument[] rows) => await database.GetCollection<BsonDocument>(name).InsertManyAsync(tx, rows, cancellationToken: token);
                await Insert("identities", new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                    { "subject", account.Subject }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } });
                await Insert("accountProfiles", new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 }, { "displayName", account.Name },
                    { "verifiedEmail", account.Email }, { "verifiedAt", at }, { "updatedAt", at }, { "version", 1L } });
                await Insert("memberships", memberships.Select(membership => new BsonDocument { { "_id", membership.Id }, { "schemaVersion", 1 },
                    { "userId", membership.UserId }, { "scope", ScopeDocument(membership.Scope) }, { "status", "active" }, { "approvedBy", membership.ApprovedBy },
                    { "effectiveAt", at }, { "version", 1L } }).ToArray());
                await Insert("authorizationGrants", grants.Select(grant => new BsonDocument { { "_id", grant.Id }, { "schemaVersion", 1 }, { "userId", grant.UserId },
                    { "membershipId", grant.MembershipId }, { "templateId", grant.TemplateId }, { "templateVersion", grant.TemplateVersion },
                    { "permissions", new BsonArray(grant.Permissions) }, { "scope", ScopeDocument(grant.Scope) }, { "status", "active" },
                    { "requestedBy", grant.RequestedBy }, { "approvedBy", grant.ApprovedBy }, { "reason", grant.Reason }, { "effectiveAt", at },
                    { "version", 1L }, { "delegablePermissions", new BsonArray() } }).ToArray());
                if (buying.Length > 0)
                    await Insert("catalogEntitlements", buying.Select((grant, index) => new BsonDocument { { "_id", entitlementIds[index] }, { "schemaVersion", 1 },
                        { "userId", account.Id }, { "membershipId", memberships[Array.IndexOf(scopes, grant.Scope)].Id }, { "assortmentId", 501L },
                        { "audienceScope", ScopeDocument(grant.Scope) }, { "status", "active" }, { "effectiveAt", at }, { "expiresAt", BsonNull.Value },
                        { "revokedAt", BsonNull.Value }, { "version", 1L }, { "requestedBy", 2001L }, { "approvedBy", 2002L }, { "approvalId", 100000 + entitlementIds[index] },
                        { "approvalVersion", 1L }, { "approvedAt", at }, { "fence", 0L } }).ToArray());
                await PilotBootstrap.InsertSyntheticPersonAsync(database, tx, account, at, token);
                return true;
            }, ct);
            added.Add(persona.Name);
        }
        if (added.Count > 0) Console.WriteLine("Persona ditambahkan (" + PilotBoundary.Profile + "): " + string.Join(", ", added));
    }

    private static BsonDocument ScopeDocument(GrantScope scope)
    {
        var value = new BsonDocument("kind", char.ToLowerInvariant(scope.Kind.ToString()[0]) + scope.Kind.ToString()[1..]);
        void Optional(string key, long? id) { if (id is { } present) value.Add(key, present); }
        Optional("organizationId", scope.OrganizationId); Optional("branchId", scope.BranchId); Optional("divisionId", scope.DivisionId);
        Optional("shopId", scope.ShopId); Optional("warehouseId", scope.WarehouseId); Optional("channelId", scope.ChannelId);
        return value;
    }
}

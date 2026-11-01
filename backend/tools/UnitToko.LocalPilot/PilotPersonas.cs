using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.LocalPilot;

/// <summary>One grant of a persona: an existing role template, its finance packs and named additions, in one pilot scope, minus the
/// template permissions this persona must not hold (<see cref="Removed"/>, e.g. the proposing side of a pair it approves).</summary>
public sealed record PersonaGrant(string TemplateId, string[] FinancePacks, string[] Additions, GrantScope Scope, string[] Removed)
{
    public ImmutableArray<string> Permissions => [.. RoleTemplates.Materialize(TemplateId, FinancePacks, Additions).Permissions.Except(Removed, StringComparer.Ordinal)];
}

/// <summary>A review persona. <c>tamu</c> is the signed-out visitor: no account, no grant, no session.</summary>
public sealed record PilotPersona(string Name, long AccountId, string Label, PersonaGrant[] Grants)
{
    public bool SignedIn => Grants.Length > 0;
}

/// <summary>What the stack reconciles on start: stored grants rewritten to the definition, definition grants not stored yet,
/// stored grants and memberships the definition no longer has, and the identity's grant version afterwards.</summary>
public sealed record GrantReconciliation(BsonDocument[] Corrected, PersonaGrant[] Missing, long[] RevokedGrants, long[] RevokedMemberships, long GrantVersion)
{
    public bool Changed => Corrected.Length + Missing.Length + RevokedGrants.Length + RevokedMemberships.Length > 0;
}

/// <summary>
/// The people who use Unit Toko, as SYNTHETIC accounts of the pilot's own organisation (10, branches 11 and 12), shop (101),
/// warehouse (102) and channel (103). Each persona is an explicit grant set built from an existing role template
/// (<see cref="RoleTemplates.Materialize"/>), its additions and the template permissions it drops, so no persona holds both sides
/// of a maker/checker pair. They join the full and review profiles; release1 keeps its four accounts.
/// </summary>
public static class PilotPersonas
{
    private static readonly GrantScope Branch11 = new(GrantScopeKind.BuyingBranch, 10, BranchId: 11);
    private static readonly GrantScope Branch12 = new(GrantScopeKind.BuyingBranch, 10, BranchId: 12);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static readonly GrantScope Warehouse = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    // The shop's courier roster: any warehouse of shop 101, as the dispatch courier picker lists it (ShipmentCourierEligibility).
    private static readonly GrantScope Courier = new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103);
    private static readonly GrantScope System = new(GrantScopeKind.System);
    private static PersonaGrant Grant(string template, GrantScope scope, params string[] additions) => new(template, [], additions, scope, []);
    private static PersonaGrant Finance(string[] packs, GrantScope scope, params string[] additions) => new("finance", packs, additions, scope, []);
    /// <summary>A grant narrowed to its additions plus the named template permissions: the rest of the template is dropped, so the persona
    /// holds only its role's work (F-027) until a job template of that exact shape exists.</summary>
    private static PersonaGrant Only(PersonaGrant grant, params string[] keep) =>
        grant with { Removed = [.. RoleTemplates.Templates[grant.TemplateId].Permissions.Where(permission => !keep.Contains(permission))] };

    public static readonly PilotPersona[] All = [
        new("tamu", 0, "Tamu (tanpa sesi)", []),
        new("pemohon", 3001, "Pemohon cabang A · SINTETIS", [Grant("buyer", Branch11, Permissions.BranchPic, Permissions.BranchOrderRequestRead,
            Permissions.BranchOrderRequestSubmit, Permissions.SubstitutionAccept, Permissions.OrganizationAddressRead)]),
        // The spending checker approves branch orders and does not place them: the buyer template's order.submit is dropped.
        new("penyetuju-cabang", 3002, "Penyetuju belanja cabang A · SINTETIS", [Grant("buyer", Branch11, Permissions.BranchPic, Permissions.OrderSpendApprove,
            Permissions.BranchOrderRequestRead, Permissions.OrganizationAddressRead) with { Removed = [Permissions.OrderSubmit] }]),
        new("peninjau-toko", 3003, "Peninjau penerimaan toko · SINTETIS", [Grant("store-head", Shop, Permissions.OrderAccept, Permissions.BranchOrderRequestRead,
            Permissions.BranchOrderRequestDecide, Permissions.OrderCommercialPolicyRead, Permissions.AnomalyRead, Permissions.OrganizationAddressRead)]),
        // Catalog maker/checker: petugas-katalog proposes prices and the guest catalog; peninjau-katalog (no pricing.manage) approves them.
        new("petugas-katalog", 3004, "Petugas katalog · SINTETIS", [Grant("store-head", Shop, Permissions.CatalogPublicPropose)]),
        new("peninjau-katalog", 3005, "Peninjau harga dan katalog publik · SINTETIS", [Only(Grant("store-staff", Shop, Permissions.PricingApprove, Permissions.CatalogPublicApprove), Permissions.CatalogRead, Permissions.OrderRead)]),
        new("petugas-gudang", 3006, "Petugas gudang (penyiapan dan penerimaan) · SINTETIS", [Grant("store-staff", Warehouse, Permissions.StockReleasePropose,
            Permissions.StockConvert, Permissions.SubstitutionPropose, Permissions.ReservationRelease, Permissions.ProcurementContainerReceive, Permissions.ProcurementInspect,
            Permissions.ProcurementInspectionEvidenceRead, Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose, Permissions.OrganizationAddressRead)]),
        // The dispatch page needs shipment.dispatch (store-staff) and shipment.assign together.
        new("petugas-kirim", 3007, "Petugas pengiriman · SINTETIS", [Grant("store-staff", Warehouse, Permissions.ShipmentAssign, Permissions.OrganizationAddressRead)]),
        new("kurir", 3008, "Kurir · SINTETIS", [Grant("courier", Courier)]),
        // Finance maker: invoice release proposal, invoice issue, bank statement staging (the maker side of bank receipt confirmation and disbursement
        // verification), receiving accounts and stock valuation proposals. It reads payment reviews to allocate payments pemeriksa-pembayaran verified
        // (F-007, owner decision 1 Nov; no payment.verify), and still has no complaints pages (F-027).
        new("keuangan-tagihan", 3009, "Keuangan penagihan · SINTETIS", [Finance(["billing"], Shop, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleasePropose, Permissions.PaymentReviewRead,
            Permissions.BankStatementRead, Permissions.BankStatementStage, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountPropose, Permissions.ProcurementStockValuationRead,
            Permissions.ProcurementStockValuationPropose, Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead,
            Permissions.ProcurementRead, Permissions.StockRead, Permissions.OrderRead, Permissions.OrganizationAddressRead)]),
        // Finance checker: payment verification, invoice release approval, bank receipt confirmation, receiving account decisions, valuation, stock release and funding
        // decisions. It reads bank statements and does not stage them: no persona holds both sides of a maker/checker pair.
        new("pemeriksa-pembayaran", 3010, "Pemeriksa pembayaran · SINTETIS", [Finance(["accounting"], Shop, Permissions.InvoiceRead, Permissions.PaymentReviewRead,
            Permissions.PaymentReviewCorrect, Permissions.PaymentReviewManual, Permissions.PaymentVerify, Permissions.BankReceiptRead, Permissions.BankReceiptConfirm,
            Permissions.BankStatementRead, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleaseApprove,
            Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove, Permissions.ProcurementStockValuationRead,
            Permissions.ProcurementStockValuationDecide, Permissions.StockReleaseApprove, Permissions.FundingRead, Permissions.FundingApprove,
            Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.ProcurementSettlementRead,
            Permissions.ProcurementRead, Permissions.StockRead, Permissions.AccountingBookRead, Permissions.ReportRead, Permissions.OrganizationAddressRead)]),
        // Procurement maker/checker: pengadaan prepares (store-staff); penyetuju-pengadaan approves (store-head, no procurement.prepare).
        new("pengadaan", 3011, "Petugas pengadaan · SINTETIS", [Grant("store-staff", Shop, Permissions.SupplierChangePropose, Permissions.ProcurementSupplierDocumentRecord,
            Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankPropose,
            Permissions.SupplierClaim, Permissions.FundingRead, Permissions.ProcurementInspectionEvidenceRead, Permissions.ProcurementQualityAcceptanceRead,
            Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose, Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnPropose,
            Permissions.OrganizationAddressRead)]),
        // The procurement checker decides supplier claims and does not raise them: the store-head template's supplier.claim is dropped.
        new("penyetuju-pengadaan", 3012, "Penyetuju pengadaan · SINTETIS", [Grant("store-head", Shop, Permissions.SupplierChangeApprove,
            Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptanceDecide, Permissions.ProcurementSupplierDocumentEvidenceRead,
            Permissions.ProcurementInspectionEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankApprove, Permissions.SupplierClaimApprove,
            Permissions.FundingRead, Permissions.ProcurementPayableRead, Permissions.ProcurementPayableDecide, Permissions.ProcurementAdvanceReturnRead,
            Permissions.ProcurementAdvanceReturnDecide, Permissions.OrganizationAddressRead) with { Removed = [Permissions.SupplierClaim] }]),
        // Access maker: proposes invitations, memberships, access and organisation changes; penyetuju-akses approves them.
        new("admin-akses", 3013, "Admin akses · SINTETIS", [Grant("system-admin", System, Permissions.PersonReviewRead)]),
        // Access checker: the system-admin template without its proposing permissions, plus the approvals.
        new("penyetuju-akses", 3015, "Penyetuju akses · SINTETIS", [Grant("system-admin", System, Permissions.AccessApprove, Permissions.AccessRevoke,
            Permissions.MembershipApprove, Permissions.OrganizationApprove, Permissions.PersonReviewRead) with { Removed = [Permissions.AccessPropose,
            Permissions.MembershipPropose, Permissions.InviteManage, Permissions.RecoveryInitiate, Permissions.TechnicalConfigure] }]),
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
            AuthorizationStatus.Active, 2001, 2002, Reason(persona), at, DelegablePermissions: [])).ToArray();
        return (memberships, grants);
    }
    private static string Reason(PilotPersona persona) => "SYNTHETIC review persona " + persona.Name + "; template grant, not a real assignment";

    /// <summary>Compares a persona's stored grants and memberships with its definition. A stored active grant matches a definition
    /// grant with the same template and scope; a match with other permissions is rewritten (its version moves), an unmatched
    /// stored grant or membership is revoked, an unmatched definition grant is added. Any change moves the identity's grant version,
    /// so sessions and snapshots taken against the old grants stop authorizing.</summary>
    public static GrantReconciliation Reconcile(PilotPersona persona, BsonDocument identity, IReadOnlyList<BsonDocument> grants, IReadOnlyList<BsonDocument> memberships)
    {
        var active = grants.Where(row => row["userId"].AsInt64 == persona.AccountId && row["status"] == "active").ToList();
        var corrected = new List<BsonDocument>(); var missing = new List<PersonaGrant>();
        foreach (var wanted in persona.Grants)
        {
            var scope = ScopeDocument(wanted.Scope);
            var stored = active.FirstOrDefault(row => row["templateId"] == wanted.TemplateId && row["scope"].AsBsonDocument.Equals(scope));
            if (stored is null) { missing.Add(wanted); continue; }
            active.Remove(stored);
            var held = stored["permissions"].AsBsonArray.Select(value => value.AsString).Order(StringComparer.Ordinal);
            if (held.SequenceEqual(wanted.Permissions.Order(StringComparer.Ordinal))) continue;
            var update = stored.DeepClone().AsBsonDocument;
            update["permissions"] = new BsonArray(wanted.Permissions);
            update["version"] = stored["version"].ToInt64() + 1;
            corrected.Add(update);
        }
        var scopes = persona.Grants.Select(grant => ScopeDocument(grant.Scope)).ToArray();
        var revokedMemberships = memberships.Where(row => row["userId"].AsInt64 == persona.AccountId && row["status"] == "active" &&
            !scopes.Any(scope => scope.Equals(row["scope"].AsBsonDocument))).Select(row => row["_id"].AsInt64).ToArray();
        var plan = new GrantReconciliation([.. corrected], [.. missing], [.. active.Select(row => row["_id"].AsInt64)], revokedMemberships, identity["grantVersion"].ToInt64());
        return plan.Changed ? plan with { GrantVersion = plan.GrantVersion + 1 } : plan;
    }

    /// <summary>On every start: adds each persona not stored yet (identity, profile, memberships, grants, catalog entitlement for
    /// buying scopes and the synthetic person evidence the independent-person rules read) and reconciles every stored persona to its
    /// definition. Ids come from the platform's counters.</summary>
    public static async Task SeedAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, CancellationToken ct)
    {
        var identities = database.GetCollection<BsonDocument>("identities");
        var grantRows = database.GetCollection<BsonDocument>("authorizationGrants");
        var membershipRows = database.GetCollection<BsonDocument>("memberships");
        var ids = new IdAllocator(database);
        var unit = new MongoUnitOfWork(client);
        var added = new List<string>(); var reconciled = new List<string>();
        foreach (var persona in SignedIn)
        {
            var account = secrets.Accounts.SingleOrDefault(item => item.Id == persona.AccountId)
                ?? throw new InvalidOperationException("Pilot account manifest is missing persona " + persona.Name + ".");
            var at = DateTime.UtcNow.AddMinutes(-1);
            var identity = await identities.Find(new BsonDocument("_id", persona.AccountId)).SingleOrDefaultAsync(ct);
            if (identity is null)
            {
                var plan = await PlanInsertAsync(ids, persona, persona.Grants, [], ct);
                await unit.ExecuteAsync(async (tx, token) =>
                {
                    var rows = database.GetCollection<BsonDocument>("identities");
                    await rows.InsertOneAsync(tx, new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                        { "subject", account.Subject }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } }, cancellationToken: token);
                    await database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(tx, new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 },
                        { "displayName", account.Name }, { "verifiedEmail", account.Email }, { "verifiedAt", at }, { "updatedAt", at }, { "version", 1L } }, cancellationToken: token);
                    await InsertAsync(database, tx, persona, plan, at, token);
                    await PilotBootstrap.InsertSyntheticPersonAsync(database, tx, account, at, token);
                    return true;
                }, ct);
                added.Add(persona.Name);
                continue;
            }
            var reconciliation = Reconcile(persona, identity, await grantRows.Find(new BsonDocument("userId", persona.AccountId)).ToListAsync(ct),
                await membershipRows.Find(new BsonDocument("userId", persona.AccountId)).ToListAsync(ct));
            if (!reconciliation.Changed) continue;
            var reuse = (await membershipRows.Find(new BsonDocument { { "userId", persona.AccountId }, { "status", "active" } }).ToListAsync(ct))
                .Where(row => !reconciliation.RevokedMemberships.Contains(row["_id"].AsInt64)).ToArray();
            var inserts = await PlanInsertAsync(ids, persona, reconciliation.Missing, reuse, ct);
            await unit.ExecuteAsync(async (tx, token) =>
            {
                foreach (var row in reconciliation.Corrected)
                    if ((await grantRows.ReplaceOneAsync(tx, new BsonDocument { { "_id", row["_id"] }, { "status", "active" }, { "version", row["version"].ToInt64() - 1 } }, row,
                        cancellationToken: token)).ModifiedCount != 1) throw new InvalidOperationException("Persona grant changed during reconciliation; start again.");
                var revoke = Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", at).Inc("version", 1L);
                foreach (var id in reconciliation.RevokedGrants)
                    await grantRows.UpdateOneAsync(tx, new BsonDocument { { "_id", id }, { "status", "active" } }, revoke, cancellationToken: token);
                foreach (var id in reconciliation.RevokedMemberships)
                    await membershipRows.UpdateOneAsync(tx, new BsonDocument { { "_id", id }, { "status", "active" } }, revoke, cancellationToken: token);
                await InsertAsync(database, tx, persona, inserts, at, token);
                if ((await identities.UpdateOneAsync(tx, new BsonDocument { { "_id", persona.AccountId }, { "grantVersion", identity["grantVersion"] } },
                    Builders<BsonDocument>.Update.Set("grantVersion", reconciliation.GrantVersion), cancellationToken: token)).ModifiedCount != 1)
                    throw new InvalidOperationException("Persona identity changed during reconciliation; start again.");
                return true;
            }, ct);
            reconciled.Add(persona.Name);
        }
        if (added.Count > 0) Console.WriteLine("Persona ditambahkan (" + PilotBoundary.Profile + "): " + string.Join(", ", added));
        if (reconciled.Count > 0) Console.WriteLine("Akses persona diselaraskan (" + PilotBoundary.Profile + "): " + string.Join(", ", reconciled));
    }

    /// <summary>The stored grants as the access screens see them, for the harness index (<c>personas.json</c>).</summary>
    public static async Task<IReadOnlyList<PersonaGrantView>> DescribeAsync(IMongoDatabase database, PilotPersona persona, CancellationToken ct) =>
        (await database.GetCollection<BsonDocument>("authorizationGrants").Find(new BsonDocument { { "userId", persona.AccountId }, { "status", "active" } })
            .Sort(new BsonDocument("_id", 1)).ToListAsync(ct))
        .Select(row => new PersonaGrantView(row["templateId"].AsString, row["scope"].AsBsonDocument.ToDictionary(),
            row["permissions"].AsBsonArray.Select(value => value.AsString).Order(StringComparer.Ordinal).ToArray())).ToArray();

    private sealed record InsertPlan((PersonaGrant Grant, long GrantId, long MembershipId, bool NewMembership, long? EntitlementId)[] Rows);
    private static async Task<InsertPlan> PlanInsertAsync(IdAllocator ids, PilotPersona persona, IReadOnlyList<PersonaGrant> grants, IReadOnlyList<BsonDocument> memberships,
        CancellationToken ct)
    {
        var known = new Dictionary<BsonDocument, long>(); foreach (var row in memberships) known.TryAdd(row["scope"].AsBsonDocument, row["_id"].AsInt64);
        var rows = new List<(PersonaGrant, long, long, bool, long?)>();
        foreach (var grant in grants)
        {
            var scope = ScopeDocument(grant.Scope);
            var fresh = !known.TryGetValue(scope, out var membership);
            if (fresh) { membership = (await ids.NextAsync("memberships", ct)).Value; known[scope] = membership; }
            var grantId = (await ids.NextAsync("authorizationGrants", ct)).Value;
            long? entitlement = grant.Scope.Kind == GrantScopeKind.BuyingBranch ? (await ids.NextAsync("catalogEntitlements", ct)).Value : null;
            rows.Add((grant, grantId, membership, fresh, entitlement));
        }
        return new([.. rows]);
    }

    private static async Task InsertAsync(IMongoDatabase database, IClientSessionHandle tx, PilotPersona persona, InsertPlan plan, DateTime at, CancellationToken token)
    {
        foreach (var (grant, grantId, membership, fresh, entitlement) in plan.Rows)
        {
            var scope = ScopeDocument(grant.Scope);
            if (fresh)
                await database.GetCollection<BsonDocument>("memberships").InsertOneAsync(tx, new BsonDocument { { "_id", membership }, { "schemaVersion", 1 },
                    { "userId", persona.AccountId }, { "scope", scope }, { "status", "active" }, { "approvedBy", 2002L }, { "effectiveAt", at }, { "version", 1L } },
                    cancellationToken: token);
            await database.GetCollection<BsonDocument>("authorizationGrants").InsertOneAsync(tx, new BsonDocument { { "_id", grantId }, { "schemaVersion", 1 },
                { "userId", persona.AccountId }, { "membershipId", membership }, { "templateId", grant.TemplateId }, { "templateVersion", RoleTemplates.CurrentVersion },
                { "permissions", new BsonArray(grant.Permissions) }, { "scope", scope }, { "status", "active" }, { "requestedBy", 2001L }, { "approvedBy", 2002L },
                { "reason", Reason(persona) }, { "effectiveAt", at }, { "version", 1L }, { "delegablePermissions", new BsonArray() } }, cancellationToken: token);
            if (entitlement is { } id)
                await database.GetCollection<BsonDocument>("catalogEntitlements").InsertOneAsync(tx, new BsonDocument { { "_id", id }, { "schemaVersion", 1 },
                    { "userId", persona.AccountId }, { "membershipId", membership }, { "assortmentId", 501L }, { "audienceScope", scope }, { "status", "active" },
                    { "effectiveAt", at }, { "expiresAt", BsonNull.Value }, { "revokedAt", BsonNull.Value }, { "version", 1L }, { "requestedBy", 2001L },
                    { "approvedBy", 2002L }, { "approvalId", 100000 + id }, { "approvalVersion", 1L }, { "approvedAt", at }, { "fence", 0L } }, cancellationToken: token);
        }
    }

    /// <summary>A grant or membership scope as the platform stores it (kind in camel case, only the ids it has).</summary>
    public static BsonDocument ScopeDocument(GrantScope scope)
    {
        var value = new BsonDocument("kind", char.ToLowerInvariant(scope.Kind.ToString()[0]) + scope.Kind.ToString()[1..]);
        void Optional(string key, long? id) { if (id is { } present) value.Add(key, present); }
        Optional("organizationId", scope.OrganizationId); Optional("branchId", scope.BranchId); Optional("divisionId", scope.DivisionId);
        Optional("shopId", scope.ShopId); Optional("warehouseId", scope.WarehouseId); Optional("channelId", scope.ChannelId);
        return value;
    }
}

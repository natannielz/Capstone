using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.LocalPilot;

public static class PilotBootstrap
{
    private const string Marker = "localPilotMetadata";
    public static async Task InitializeAsync(MongoClient client, PilotSecrets secrets, CancellationToken ct)
    {
        PilotBoundary.RequireDatabase(PilotBoundary.MongoUri, PilotBoundary.DatabaseName);
        await InitializeDatabaseAsync(client, client.GetDatabase(PilotBoundary.DatabaseName), secrets, ct);
    }

    /// <summary>The synthetic organisation: buying organisation 10 with branches 11 and 12; cooperative 100 with shop 101, its warehouse 102
    /// and channel 103.</summary>
    internal static readonly (long Id, ScopeNodeKind Kind, long? Parent, string Name)[] Nodes = [
        (10, ScopeNodeKind.BuyingOrganization, null, "Organisasi pemohon SINTETIS"), (11, ScopeNodeKind.Branch, 10, "Cabang A SINTETIS"),
        (12, ScopeNodeKind.Branch, 10, "Cabang B SINTETIS"), (100, ScopeNodeKind.FulfillmentOrganization, null, "Koperasi SINTETIS"),
        (101, ScopeNodeKind.Shop, 100, "Toko pilot SINTETIS"), (102, ScopeNodeKind.Warehouse, 101, "Gudang pilot SINTETIS"),
        (103, ScopeNodeKind.Channel, 100, "Kanal pilot SINTETIS")];
    /// <summary>The two synthetic bootstrap people who name the organisation through the application: different people, maker and checker.</summary>
    private static readonly (long Id, string Name, string Permission)[] ProfileActors = [
        (890001, "Penyiap organisasi bootstrap · SINTETIS", Permissions.TechnicalConfigure),
        (890002, "Pemeriksa organisasi bootstrap · SINTETIS", Permissions.OrganizationApprove)];

    /// <summary>The two bootstrap people who name the organisation, added when missing (a database seeded before G-034 has neither). They are
    /// synthetic, hold nothing beyond technical.configure and organization.approve, and are different people.</summary>
    private static async Task EnsureProfileActorsAsync(IMongoDatabase database, DateTime at, CancellationToken ct)
    {
        var system = new BsonDocument("kind", "system");
        async Task Add(string table, BsonDocument row)
        {
            var fields = row.DeepClone().AsBsonDocument; fields.Remove("_id");
            await database.GetCollection<BsonDocument>(table).UpdateOneAsync(new BsonDocument("_id", row["_id"]), new BsonDocument("$setOnInsert", fields), new UpdateOptions { IsUpsert = true }, ct);
        }
        foreach (var (id, name, permission) in ProfileActors)
        {
            await Add("accountProfiles", new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "displayName", name },
                { "verifiedEmail", "bootstrap-" + id + "@unit-toko-pilot.home.arpa" }, { "verifiedAt", at }, { "updatedAt", at }, { "version", 1L } });
            await Add("memberships", new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "userId", id }, { "scope", system }, { "status", "active" },
                { "approvedBy", 2002L }, { "effectiveAt", at }, { "version", 1L } });
            await Add("authorizationGrants", new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "userId", id }, { "membershipId", id },
                { "templateId", "system-admin" }, { "templateVersion", 1 }, { "permissions", new BsonArray { permission } }, { "scope", system },
                { "status", "active" }, { "requestedBy", 2001L }, { "approvedBy", 2002L }, { "reason", "SYNTHETIC local pilot bootstrap: names the synthetic organisation" },
                { "effectiveAt", at }, { "version", 1L }, { "delegablePermissions", new BsonArray() } });
        }
    }

    /// <summary>Every node's profile (name and code) approved through the real organisation service: 890001 proposes, 890002 approves, so each
    /// profile carries the proposal, the proposed and approved history and the receipts that organisation, address, policy, procurement and
    /// accounting first reads require (G-034). A new database gets each profile created. A database seeded before the fix, whose profiles
    /// were written straight into it without any of that, gets each profile revised once with the same name and code (version 1 to 2), so
    /// nothing is deleted or rewritten. Idempotent: a profile whose current version already has its approval is left alone, and our own
    /// pending proposal is approved instead of proposing again.</summary>
    private static async Task OrganizationProfilesAsync(IMongoDatabase database, MongoClient client, DateTime at, CancellationToken ct)
    {
        await EnsureProfileActorsAsync(database, at, ct);
        var clock = new SystemClock(); var unit = new MongoUnitOfWork(client);
        var organizations = new OrganizationMasterDataService(database, new AuthorizationStore(database, unit, clock), new IdAllocator(database), clock);
        ActorContext Actor(long id) => new(id, PilotBoundary.Issuer, "synthetic-bootstrap-reviewer-" + id, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        static AuthorizationCommandMetadata Meta(string key) => new("local-pilot.org-profile-v1." + key, "SYNTHETIC: nama organisasi pilot, diajukan dan diperiksa dua orang berbeda", "local-pilot-organization");
        var profiles = database.GetCollection<BsonDocument>("organizationProfiles");
        var proposals = database.GetCollection<BsonDocument>("organizationProposals");
        var history = database.GetCollection<BsonDocument>("authorizationHistory");
        foreach (var (id, kind, parent, name) in Nodes)
        {
            var profile = await profiles.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            var version = profile?["version"].AsInt64 ?? 0;
            if (profile is not null && await history.Find(new BsonDocument { { "operation", "organization.approved" }, { "after.profile._id", id }, { "after.profile.version", version } }).AnyAsync(ct)) continue;
            var pending = await proposals.Find(new BsonDocument { { "scopeId", id }, { "status", "pending" }, { "requestedBy", 890001L }, { "expectedProfileVersion", version } }).FirstOrDefaultAsync(ct);
            var scopeVersion = (await database.GetCollection<BsonDocument>("authorizationScopes").Find(new BsonDocument("_id", id)).SingleAsync(ct))["version"].ToInt64();
            var key = id + ".v" + version;
            var proposalId = pending?["_id"].AsInt64 ?? (await organizations.ProposeAsync(Actor(890001), new OrganizationChangeRequest(OrganizationChangeKind.UpdateProfile, kind, parent,
                profile?["code"].AsString ?? "SYN-" + id, profile?["name"].AsString ?? name, id, scopeVersion, version), Meta(key + ".propose"), ct)).Id;
            await organizations.ApproveAsync(Actor(890002), proposalId, 1, Meta(key + ".approve"), ct);
        }
    }

    /// <summary>The same approvals for a full or review database that is already set up, on its next start (the persistent Release 1 pilot is
    /// left as it is). A failure is reported and does not stop the start: every step is its own transaction, so nothing is half-written, and
    /// the pages that need the approvals keep answering 409 until the next start retries.</summary>
    private static async Task UpgradeOrganizationProfilesAsync(IMongoDatabase database, MongoClient client, DateTime at, CancellationToken ct)
    {
        if (!PilotBoundary.IsFullRelease) return;
        try { await OrganizationProfilesAsync(database, client, at, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine("Nama organisasi pilot belum dapat disahkan lewat layanan organisasi (" + exception.GetType().Name + ": " + exception.Message + "). Mulai ulang pilot untuk mencoba lagi.");
        }
    }

    /// <summary>The bootstrap on a database the caller has already verified: the pilot's own (above), or a test's disposable one.</summary>
    internal static async Task InitializeDatabaseAsync(MongoClient client, IMongoDatabase database, PilotSecrets secrets, CancellationToken ct)
    {
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var markers = database.GetCollection<BsonDocument>(Marker);
        var marker = names.Contains(Marker) ? await markers.Find(new BsonDocument("_id", PilotBoundary.Profile)).SingleOrDefaultAsync(ct) : null;
        if (marker is null)
        {
            if (names.Count != 0) throw new InvalidOperationException("Refusing to bootstrap an existing unmarked database.");
            marker = new BsonDocument { { "_id", PilotBoundary.Profile }, { "instanceId", secrets.InstanceId }, { "synthetic", true },
                { "createdAt", DateTime.UtcNow }, { "phase", "created" } };
            await markers.InsertOneAsync(marker, cancellationToken: ct);
        }
        if (marker["instanceId"] != secrets.InstanceId || marker["synthetic"] != true)
            throw new InvalidOperationException("Pilot database belongs to a different private launcher state. Restore matching state; do not reset it.");
        var clock = new SystemClock();
        await new CollectionMigrations(database, clock).ApplyAsync(ct);
        if (marker["phase"] == "complete")
        {
            await UpgradeOrganizationProfilesAsync(database, client, marker["createdAt"].ToUniversalTime(), ct);
            await GrowGrantsAsync(database, secrets, ct);
            await PilotCatalog.ExpandAsync(database, client, secrets, markers, PilotBoundary.Profile, ct);
            await SeedAfterCatalogAsync(database, client, secrets, markers, ct);
            return;
        }
        // The first bootstrap covers the four original accounts; personas join afterwards through SeedAfterCatalogAsync.
        var originals = secrets.Accounts.Where(x => PilotPersonas.OriginalAccountIds.Contains(x.Id)).ToArray();
        var unit = new MongoUnitOfWork(client);
        var at = marker["createdAt"].ToUniversalTime();
        if (marker["phase"] == "created")
        {
            await unit.ExecuteAsync(async (tx, token) =>
            {
                async Task Insert(string name, IEnumerable<BsonDocument> rows) => await database.GetCollection<BsonDocument>(name).InsertManyAsync(tx, rows, cancellationToken: token);
                // The organisation tree itself. Each node's name and code (its profile) is then approved through the application by
                // OrganizationProfilesAsync, so it carries the approval history every first read checks (G-034).
                await Insert("authorizationScopes", Nodes.Select(x => { var row = new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 },
                    { "kind", char.ToLowerInvariant(x.Kind.ToString()[0]) + x.Kind.ToString()[1..] }, { "status", "active" }, { "version", 1L }, { "fence", 0L } };
                    if (x.Parent is { } parent) row["parentId"] = parent; return row; }));
                // The two bootstrap people who name the organisation have identities below; their account, membership and grant come from EnsureProfileActorsAsync.
                await Insert("identities", originals.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                    { "subject", x.Subject }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } }));
                await Insert("identities", new long[] { 890001, 890002 }.Select(id => new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                    { "subject", "synthetic-bootstrap-reviewer-" + id }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } }));
                await Insert("accountProfiles", originals.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "displayName", x.Name },
                    { "verifiedEmail", x.Email }, { "verifiedAt", at }, { "updatedAt", at }, { "version", 1L } }));
                await Insert("memberships", originals.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id },
                    { "scope", Scope(x.Id) }, { "status", "active" }, { "approvedBy", x.Id == 2002 ? 2001L : 2002L }, { "effectiveAt", at }, { "version", 1L } }));
                await Insert("authorizationGrants", originals.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id }, { "membershipId", x.Id },
                    { "templateId", x.Id < 2000 ? "buyer" : "store-staff" }, { "templateVersion", 1 }, { "permissions", new BsonArray(PermissionsFor(x.Id)) }, { "scope", Scope(x.Id) },
                    { "status", "active" }, { "requestedBy", x.Id >= 2000 ? 1001L : 2001L }, { "approvedBy", x.Id == 2002 ? 2001L : 2002L },
                    { "reason", "SYNTHETIC local bootstrap; not an independently approved real assignment" }, { "effectiveAt", at }, { "version", 1L }, { "delegablePermissions", new BsonArray() } }));
                await Insert("authorizationGrants", [new BsonDocument { { "_id", 2101L }, { "schemaVersion", 1 }, { "userId", 2001L }, { "membershipId", 2001L },
                    { "templateId", "store-staff" }, { "templateVersion", 1 }, { "permissions", new BsonArray { "order.request.read", "order.request.decide" } },
                    { "scope", new BsonDocument { { "kind", "fulfillmentWarehouse" }, { "organizationId", 100L }, { "shopId", 101L }, { "warehouseId", 102L }, { "channelId", 103L } } },
                    { "status", "active" }, { "requestedBy", 1001L }, { "approvedBy", 2002L }, { "reason", "SYNTHETIC local pilot staff assignment" },
                    { "effectiveAt", at }, { "version", 1L }, { "delegablePermissions", new BsonArray() } }]);
                await Insert("catalogAssortments", [new BsonDocument { { "_id", 501L }, { "schemaVersion", 1 }, { "ownerScope", new BsonDocument { { "kind", "fulfillmentWarehouse" },
                    { "organizationId", 100L }, { "shopId", 101L }, { "warehouseId", 102L }, { "channelId", 103L } } }, { "status", "active" }, { "version", 1L }, { "fence", 0L } }]);
                await Insert("catalogEntitlements", originals.Where(x => x.Id < 2000).Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id },
                    { "membershipId", x.Id }, { "assortmentId", 501L }, { "audienceScope", Scope(x.Id) }, { "status", "active" }, { "effectiveAt", at }, { "expiresAt", BsonNull.Value },
                    { "revokedAt", BsonNull.Value }, { "version", 1L }, { "requestedBy", 2001L }, { "approvedBy", 2002L }, { "approvalId", x.Id + 10000 }, { "approvalVersion", 1L }, { "approvedAt", at }, { "fence", 0L } }));
                // Explicitly synthetic evidence permits the real independent-person rule to run.
                // These records never leave the marked pilot database or assert real-world verification.
                await Insert("personEvidenceSources", [Reviewed(new BsonDocument { { "_id", 810000L }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
                    { "key", "synthetic-local-pilot" }, { "status", "approved" }, { "adapterId", "synthetic-local-pilot-v1" }, { "policyReference", "SYNTHETIC local pilot only; not real identity approval" } }, at, true)]);
                foreach (var account in originals) await InsertSyntheticPersonAsync(database, tx, account, at, token);
                foreach (var (name, floor) in new[] { ("authorizationScopes", 103L), ("identities", 890002L), ("memberships", 2002L),
                    ("authorizationGrants", 2101L), ("catalogAssortments", 501L), ("catalogEntitlements", 1002L) })
                    await database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(tx, new BsonDocument("_id", name),
                        Builders<BsonDocument>.Update.Max("value", floor), new UpdateOptions { IsUpsert = true }, token);
                await markers.UpdateOneAsync(tx, new BsonDocument("_id", PilotBoundary.Profile), Builders<BsonDocument>.Update.Set("phase", "identities"), cancellationToken: token);
                return true;
            }, ct);
        }
        await OrganizationProfilesAsync(database, client, at, ct);
        var actor = new ActorContext(2001, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 2001).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
        var service = new CatalogAuthoringService(database, unit, new(database), clock);
        var owner = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        static AuthorizationCommandMetadata Metadata(string key) => new("local-pilot." + key, "SYNTHETIC local pilot catalog bootstrap; no real approval or stock", "local-pilot-bootstrap");
        var category = await service.SaveCategoryAsync(actor, new(owner, null, 0, "SYNTHETIC", "Contoh pilot SINTETIS"), Metadata("category"), ct);
        var products = new[] { ("SYN-PENCIL-12", "Pensil contoh SINTETIS", "Dus isi 12", 12), ("SYN-NOTEBOOK-6", "Buku tulis contoh SINTETIS", "Pak isi 6", 6), ("SYN-PEN-1", "Pulpen contoh SINTETIS", "Satuan", 1) };
        for (var index = 0; index < products.Length; index++)
        {
            var item = products[index];
            var family = await service.CreateFamilyAsync(actor, new(owner, category.Id, item.Item2, "Data contoh sintetis untuk uji permintaan cabang. Bukan persediaan atau harga nyata.", []), Metadata("family-" + index), ct);
            var sku = await service.CreateSkuAsync(actor, new(family.Id, family.Version, item.Item1, "synthetic-pilot-item-" + index,
                new DefinitionInput(item.Item3, index == 2 ? "loose" : "sealed", "piece", item.Item4, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1)), Metadata("sku-" + index), ct);
            await service.SetPublicationsAsync(actor, new(sku.Id, sku.Version, [new(501)], [new(new(501), index + 1)]), Metadata("publish-" + index), ct);
            if (PilotBoundary.IsFullRelease)
            {
                // Synthetic prices through the real maker/checker price service so requesters can prepare order drafts.
                var prices = new CatalogPriceService(database, unit, new(database), clock);
                var reviewer = new ActorContext(2002, PilotBoundary.Issuer, secrets.Accounts.Single(x => x.Id == 2002).Subject, 1, clock.UtcNow, FactorAssurance.MultiFactor);
                var preview = await prices.PreviewAsync(actor, sku.Id.Value, CatalogPriceAccess.Prepare, ct);
                var proposal = await prices.ProposeAsync(actor, sku.Id.Value, preview.Expected,
                    new("IDR", UnitToko.Modules.Accounting.Money.Parse(new[] { "18000.00", "27000.00", "3500.00" }[index])), Metadata("price-" + index), ct);
                await prices.DecideAsync(reviewer, proposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
                    "SYNTHETIC independent price review for the local demo", Metadata("price-approve-" + index), ct);
            }
        }
        await markers.UpdateOneAsync(new BsonDocument("_id", PilotBoundary.Profile), Builders<BsonDocument>.Update.Set("phase", "complete"), cancellationToken: ct);
        await PilotCatalog.ExpandAsync(database, client, secrets, markers, PilotBoundary.Profile, ct);
        await SeedAfterCatalogAsync(database, client, secrets, markers, ct);
    }

    /// <summary>
    /// Everything seeded after the catalog, on every start, each step idempotent (full and review profiles; release1 keeps its
    /// four accounts and has no guest samples):
    /// 1. the review personas, reconciled to their definitions;
    /// 2. the 12 guest sample products (<see cref="PilotSamples"/>), through the real maker/checker sample service.
    /// </summary>
    private static async Task SeedAfterCatalogAsync(IMongoDatabase database, MongoClient client, PilotSecrets secrets, IMongoCollection<BsonDocument> markers, CancellationToken ct)
    {
        _ = markers;
        if (!PilotBoundary.IsFullRelease) return;
        await PilotPersonas.SeedAsync(database, client, secrets, ct);
        await PilotSamples.SeedAsync(database, client, secrets, ct);
    }

    /// <summary>Synthetic person evidence for one account, so the real independent-person rules can tell the pilot's people apart.
    /// These records never leave the marked pilot database or assert real-world verification.</summary>
    public static async Task InsertSyntheticPersonAsync(IMongoDatabase database, IClientSessionHandle tx, PilotAccount account, DateTime at, CancellationToken token)
    {
        async Task Insert(string name, BsonDocument row) => await database.GetCollection<BsonDocument>(name).InsertOneAsync(tx, row, cancellationToken: token);
        var personId = 1000000 + account.Id; var evidenceId = 2000000 + account.Id; var groupId = 3000000 + account.Id;
        await Insert("personRecords", new BsonDocument { { "_id", personId }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L }, { "sourceId", 810000L },
            { "sourceSubjectKey", "synthetic-pilot-person-" + account.Id }, { "currentEvidenceId", evidenceId }, { "currentEquivalenceId", groupId }, { "status", "active" } });
        await Insert("personEvidenceVersions", Reviewed(new BsonDocument { { "_id", evidenceId }, { "schemaVersion", 1 }, { "personId", personId }, { "evidenceVersion", 1L },
            { "sourceId", 810000L }, { "sourceVersion", 1L }, { "sourceSubjectKey", "synthetic-pilot-person-" + account.Id }, { "sourceReference", "SYNTHETIC-LOCAL-ACCOUNT-" + account.Id },
            { "sourceDigest", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Subject))).ToLowerInvariant() },
            { "adapterId", "synthetic-local-pilot-v1" }, { "createdAt", at }, { "reason", "SYNTHETIC local pilot identity simulation" } }, at, true));
        await Insert("personAccountBindings", Reviewed(new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
            { "personId", personId }, { "evidenceId", evidenceId }, { "verification", "verified" }, { "status", "active" }, { "reason", "SYNTHETIC local pilot identity simulation" } }, at, true));
        await Insert("personEquivalenceGroups", Reviewed(new BsonDocument { { "_id", groupId }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
            { "canonicalPersonId", personId }, { "memberPersonIds", new BsonArray { personId } }, { "memberUserIds", new BsonArray { account.Id } }, { "status", "active" } }, at, false));
    }

    /// <summary>Permissions added to the full demo after an installation was set up reach its existing grants once. Only the
    /// permission list grows: the account's grant version stays, so the pilot's own seeding actors keep working.</summary>
    private static async Task GrowGrantsAsync(IMongoDatabase database, PilotSecrets secrets, CancellationToken ct)
    {
        if (!PilotBoundary.IsFullRelease) return;
        var grants = database.GetCollection<BsonDocument>("authorizationGrants");
        foreach (var account in secrets.Accounts.Where(x => PilotPersonas.OriginalAccountIds.Contains(x.Id)))
        {
            var grant = await grants.Find(new BsonDocument("_id", account.Id)).SingleOrDefaultAsync(ct);
            if (grant is null) continue;
            var held = grant["permissions"].AsBsonArray.Select(value => value.AsString).ToHashSet(StringComparer.Ordinal);
            var missing = PermissionsFor(account.Id).Where(permission => !held.Contains(permission)).ToArray();
            if (missing.Length == 0) continue;
            await grants.UpdateOneAsync(new BsonDocument { { "_id", account.Id }, { "permissions", grant["permissions"] } },
                Builders<BsonDocument>.Update.AddToSetEach("permissions", missing), cancellationToken: ct);
            Console.WriteLine("Akses ditambahkan untuk " + account.Name + ": " + string.Join(", ", missing));
        }
    }

    public static string[] PermissionsFor(long accountId) => PilotBoundary.IsFullRelease ? FullPermissionsFor(accountId) : accountId < 2000
        ? [Permissions.CatalogRead, "order.request.read", "order.request.submit", Permissions.BranchPic]
        : accountId == 2001 ? [Permissions.CatalogRead, Permissions.CatalogManage] : [Permissions.CatalogRead];

    // Full-workspace demo: requesters can also prepare and follow orders and payments; the staff account sees the
    // maintained operational workspaces; the second staff identity is the independent price reviewer. SYNTHETIC only.
    private static string[] FullPermissionsFor(long accountId) => accountId < 2000
        ? [Permissions.CatalogRead, "order.request.read", "order.request.submit", Permissions.BranchPic, Permissions.OrderSubmit, Permissions.OrderRead,
            Permissions.InvoiceRead, Permissions.PaymentEvidence, Permissions.ShipmentRead, Permissions.CaseRead, Permissions.CaseRequest]
        : accountId == 2001
            ? [Permissions.CatalogRead, Permissions.CatalogManage, Permissions.PricingManage, Permissions.OrderRead, Permissions.OrderAccept,
                Permissions.InvoiceRead, Permissions.PaymentReviewRead, Permissions.ShipmentRead, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch,
                Permissions.ShipmentAssign, Permissions.StockRead, Permissions.CaseRead, Permissions.BankReceiptRead, Permissions.CatalogPublicPropose]
            : [Permissions.CatalogRead, Permissions.PricingApprove, Permissions.CatalogPublicApprove];
    private static BsonDocument Scope(long accountId) => accountId < 2000
        ? new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", 10L }, { "branchId", accountId == 1001 ? 11L : 12L } }
        : new BsonDocument { { "kind", "fulfillmentShop" }, { "organizationId", 100L }, { "shopId", 101L }, { "channelId", 103L } };
    private static BsonDocument Reviewed(BsonDocument row, DateTime at, bool lifetime)
    {
        row.AddRange(new BsonDocument { { "requestedBy", 890001L }, { "approvedBy", 890002L }, { "approvalId", 890003L }, { "approvalVersion", 1L }, { "reviewedAt", at } });
        if (lifetime) row.AddRange(new BsonDocument { { "effectiveAt", at.AddMinutes(-1) }, { "expiresAt", at.AddDays(30) } });
        return row;
    }
}

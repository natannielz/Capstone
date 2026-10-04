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

namespace UnitToko.LocalPilot;

public static class PilotBootstrap
{
    private const string Marker = "localPilotMetadata";
    public static async Task InitializeAsync(MongoClient client, PilotSecrets secrets, CancellationToken ct)
    {
        PilotBoundary.RequireDatabase(PilotBoundary.MongoUri, PilotBoundary.DatabaseName);
        var database = client.GetDatabase(PilotBoundary.DatabaseName);
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var markers = database.GetCollection<BsonDocument>(Marker);
        var marker = names.Contains(Marker) ? await markers.Find(new BsonDocument("_id", "release1")).SingleOrDefaultAsync(ct) : null;
        if (marker is null)
        {
            if (names.Count != 0) throw new InvalidOperationException("Refusing to bootstrap an existing unmarked database.");
            marker = new BsonDocument { { "_id", "release1" }, { "instanceId", secrets.InstanceId }, { "synthetic", true },
                { "createdAt", DateTime.UtcNow }, { "phase", "created" } };
            await markers.InsertOneAsync(marker, cancellationToken: ct);
        }
        if (marker["instanceId"] != secrets.InstanceId || marker["synthetic"] != true)
            throw new InvalidOperationException("Pilot database belongs to a different private launcher state. Restore matching state; do not reset it.");
        var clock = new SystemClock();
        await new CollectionMigrations(database, clock).ApplyAsync(ct);
        if (marker["phase"] == "complete") return;
        var unit = new MongoUnitOfWork(client);
        var at = marker["createdAt"].ToUniversalTime();
        if (marker["phase"] == "created")
        {
            await unit.ExecuteAsync(async (tx, token) =>
            {
                async Task Insert(string name, IEnumerable<BsonDocument> rows) => await database.GetCollection<BsonDocument>(name).InsertManyAsync(tx, rows, cancellationToken: token);
                var nodes = new[] { (10L, "buyingOrganization", (long?)null, "Organisasi pemohon SINTETIS"), (11L, "branch", (long?)10, "Cabang A SINTETIS"),
                    (12L, "branch", (long?)10, "Cabang B SINTETIS"), (100L, "fulfillmentOrganization", (long?)null, "Koperasi SINTETIS"),
                    (101L, "shop", (long?)100, "Toko pilot SINTETIS"), (102L, "warehouse", (long?)101, "Gudang pilot SINTETIS"), (103L, "channel", (long?)100, "Kanal pilot SINTETIS") };
                await Insert("authorizationScopes", nodes.Select(x => { var row = new BsonDocument { { "_id", x.Item1 }, { "schemaVersion", 1 }, { "kind", x.Item2 }, { "status", "active" }, { "version", 1L }, { "fence", 0L } }; if (x.Item3 is { } parent) row["parentId"] = parent; return row; }));
                await Insert("organizationProfiles", nodes.Select(x => new BsonDocument { { "_id", x.Item1 }, { "schemaVersion", 1 }, { "kind", x.Item2 },
                    { "parentId", x.Item3 is { } parent ? new BsonInt64(parent) : BsonNull.Value }, { "code", "SYN-" + x.Item1 }, { "normalizedCode", "SYN-" + x.Item1 },
                    { "name", x.Item4 }, { "version", 1L }, { "createdBy", 2001L }, { "updatedBy", 2002L }, { "createdAt", at }, { "updatedAt", at }, { "approvedProposalId", x.Item1 + 10000 } }));
                await Insert("identities", secrets.Accounts.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                    { "subject", x.Subject }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } }));
                await Insert("identities", new long[] { 890001, 890002 }.Select(id => new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "issuer", PilotBoundary.Issuer },
                    { "subject", "synthetic-bootstrap-reviewer-" + id }, { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L } }));
                await Insert("accountProfiles", secrets.Accounts.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "displayName", x.Name },
                    { "verifiedEmail", x.Email }, { "verifiedAt", at }, { "updatedAt", at }, { "version", 1L } }));
                await Insert("memberships", secrets.Accounts.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id },
                    { "scope", Scope(x.Id) }, { "status", "active" }, { "approvedBy", x.Id == 2002 ? 2001L : 2002L }, { "effectiveAt", at }, { "version", 1L } }));
                await Insert("authorizationGrants", secrets.Accounts.Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id }, { "membershipId", x.Id },
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
                await Insert("catalogEntitlements", secrets.Accounts.Where(x => x.Id < 2000).Select(x => new BsonDocument { { "_id", x.Id }, { "schemaVersion", 1 }, { "userId", x.Id },
                    { "membershipId", x.Id }, { "assortmentId", 501L }, { "audienceScope", Scope(x.Id) }, { "status", "active" }, { "effectiveAt", at }, { "expiresAt", BsonNull.Value },
                    { "revokedAt", BsonNull.Value }, { "version", 1L }, { "requestedBy", 2001L }, { "approvedBy", 2002L }, { "approvalId", x.Id + 10000 }, { "approvalVersion", 1L }, { "approvedAt", at }, { "fence", 0L } }));
                // Explicitly synthetic evidence permits the real independent-person rule to run.
                // These records never leave the marked pilot database or assert real-world verification.
                await Insert("personEvidenceSources", [Reviewed(new BsonDocument { { "_id", 810000L }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
                    { "key", "synthetic-local-pilot" }, { "status", "approved" }, { "adapterId", "synthetic-local-pilot-v1" }, { "policyReference", "SYNTHETIC local pilot only; not real identity approval" } }, at, true)]);
                foreach (var account in secrets.Accounts)
                {
                    var personId = 1000000 + account.Id; var evidenceId = 2000000 + account.Id; var groupId = 3000000 + account.Id;
                    await Insert("personRecords", [new BsonDocument { { "_id", personId }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L }, { "sourceId", 810000L },
                        { "sourceSubjectKey", "synthetic-pilot-person-" + account.Id }, { "currentEvidenceId", evidenceId }, { "currentEquivalenceId", groupId }, { "status", "active" } }]);
                    await Insert("personEvidenceVersions", [Reviewed(new BsonDocument { { "_id", evidenceId }, { "schemaVersion", 1 }, { "personId", personId }, { "evidenceVersion", 1L },
                        { "sourceId", 810000L }, { "sourceVersion", 1L }, { "sourceSubjectKey", "synthetic-pilot-person-" + account.Id }, { "sourceReference", "SYNTHETIC-LOCAL-ACCOUNT-" + account.Id },
                        { "sourceDigest", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Subject))).ToLowerInvariant() },
                        { "adapterId", "synthetic-local-pilot-v1" }, { "createdAt", at }, { "reason", "SYNTHETIC local pilot identity simulation" } }, at, true)]);
                    await Insert("personAccountBindings", [Reviewed(new BsonDocument { { "_id", account.Id }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
                        { "personId", personId }, { "evidenceId", evidenceId }, { "verification", "verified" }, { "status", "active" }, { "reason", "SYNTHETIC local pilot identity simulation" } }, at, true)]);
                    await Insert("personEquivalenceGroups", [Reviewed(new BsonDocument { { "_id", groupId }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L },
                        { "canonicalPersonId", personId }, { "memberPersonIds", new BsonArray { personId } }, { "memberUserIds", new BsonArray { account.Id } }, { "status", "active" } }, at, false)]);
                }
                foreach (var (name, floor) in new[] { ("authorizationScopes", 103L), ("organizationProfiles", 103L), ("identities", 890002L), ("memberships", 2002L),
                    ("authorizationGrants", 2101L), ("catalogAssortments", 501L), ("catalogEntitlements", 1002L) })
                    await database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(tx, new BsonDocument("_id", name),
                        Builders<BsonDocument>.Update.Max("value", floor), new UpdateOptions { IsUpsert = true }, token);
                await markers.UpdateOneAsync(tx, new BsonDocument("_id", "release1"), Builders<BsonDocument>.Update.Set("phase", "identities"), cancellationToken: token);
                return true;
            }, ct);
        }
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
        }
        await markers.UpdateOneAsync(new BsonDocument("_id", "release1"), Builders<BsonDocument>.Update.Set("phase", "complete"), cancellationToken: ct);
    }

    public static string[] PermissionsFor(long accountId) => accountId < 2000
        ? [Permissions.CatalogRead, "order.request.read", "order.request.submit", Permissions.BranchPic]
        : accountId == 2001 ? [Permissions.CatalogRead, Permissions.CatalogManage] : [Permissions.CatalogRead];
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

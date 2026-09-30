using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Tests.Ordering;
// Synthetic channel/person policies only. No bank/provider or operational approval is asserted.
internal sealed class ReceivingAccountTestData(CatalogAuthoringTestData catalog) : IAsyncDisposable
{
    internal CatalogAuthoringTestData Catalog { get; } = catalog;
    internal ActorContext Maker { get; private set; } = null!;
    internal ActorContext Checker { get; private set; } = null!;
    internal ActorContext Approver { get; private set; } = null!;
    internal ActorContext Other { get; private set; } = null!;
    internal ActorContext Masked { get; private set; } = null!;
    internal static ReceivingAccountDetails Details => new("FIXTUREBANK", "0000123456789", "Synthetic cooperative", "IDR");
    internal static string[] PermissionsAll => [Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove];
    internal ReceivingAccountService Service => NewService();
    internal ReceivingAccountService NewService(MongoClient? client = null) {
        var selected = client ?? Catalog.Client; var database = selected.GetDatabase(Catalog.Database.DatabaseNamespace.DatabaseName);
        return new(database, new(selected), new(database), Catalog.Clock);
    }
    internal IMongoCollection<BsonDocument> Rows(string name) => Catalog.Rows(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independent bank-channel review", "synthetic-receiving-account");
    internal static async Task<ReceivingAccountTestData> CreateAsync(MongoFixture fixture) {
        var data = new ReceivingAccountTestData(await CatalogAuthoringTestData.CreateAsync(fixture));
        data.Maker = await data.Catalog.ActorAsync(7010, CatalogAuthoringTestData.Shop, PermissionsAll);
        data.Checker = await data.Catalog.ActorAsync(7020, CatalogAuthoringTestData.Shop, PermissionsAll);
        data.Approver = await data.Catalog.ActorAsync(7030, CatalogAuthoringTestData.Shop, PermissionsAll);
        data.Other = await data.Catalog.ActorAsync(7040, CatalogAuthoringTestData.OtherOwner, PermissionsAll);
        data.Masked = await data.Catalog.ActorAsync(7050, CatalogAuthoringTestData.Shop, Permissions.ReceivingAccountRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, data.Maker.UserId, data.Checker.UserId, data.Approver.UserId);
        await data.Rows(B.Policies).InsertOneAsync(new BsonDocument { { "_id", 810L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
            { "scope", new BsonDocument { { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }, { "warehouseId", BsonNull.Value } } },
            { "bankCode", Details.BankCode }, { "bankName", "Synthetic bank" }, { "method", "authenticated-bank-record" },
            { "trustedChannelReference", "synthetic-bank-portal-policy" }, { "operatingOwnerReference", "synthetic-finance-owner" },
            { "approvalReference", "synthetic-business-approval" }, { "active", true }, { "approvedAt", data.Catalog.Clock.UtcNow.AddDays(-1).UtcDateTime },
            { "expiresAt", data.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime }, { "maxObservationAgeSeconds", 3600L } });
        return data;
    }
    internal Task<BsonDocument> ProposeAsync(string key = "propose", long? accountId = null, long version = 0, ReceivingAccountDetails? details = null) =>
        Service.ProposeAsync(Maker, accountId, version, 810, 1, details ?? Details, Meta(key));
    internal Task<BsonDocument> ObserveAsync(BsonDocument proposal, ReceivingAccountDetails? details = null, string key = "observe") =>
        Service.ObserveAsync(Checker, proposal["proposalId"].AsInt64, 1, details ?? Details, "synthetic-bank-issued-confirmation", true, Meta(key));
    internal Task<BsonDocument> ApproveAsync(BsonDocument proposal, BsonDocument observed, string key = "approve") =>
        Service.DecideAsync(Approver, proposal["proposalId"].AsInt64, 1, ReceivingAccountDecision.Approve, observed["recordId"].AsInt64, Meta(key));
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { B.Heads, B.Proposals, B.Observations, B.Decisions, B.Versions, B.Policies, "idempotencyRecords", "auditEvents", "outboxEvents", "identities", "authorizationGrants", "memberships", "authorizationScopes", "identitySessions", "personAccountBindings", "personRecords", "personEquivalenceGroups", "personEvidenceSources" })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync() => Catalog.DisposeAsync();
}

using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

// All catalog/profile history comes from actual producers. Verification policies are explicit
// synthetic operator inputs in this isolated fixture; they are not real supplier verification.
internal sealed class SupplierTestData(ProcurementRequestTestData procurement) : IAsyncDisposable
{
    internal const string Heads = "supplierHeads", Proposals = "supplierProposals", Observations = "supplierObservations", Decisions = "supplierDecisions",
        Versions = "supplierVersions", Namespaces = "supplierSourceNamespaces", Policies = "supplierVerificationPolicies", Prefix = "procurement.supplier.";
    internal ProcurementRequestTestData Procurement { get; } = procurement;
    internal StoreTestData Base => Procurement.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Procurement.Unit;
    internal SupplierService Service => new(Database, Unit, new(Database), Clock, Procurement.Catalog);
    internal ActorContext Maker = null!, Checker = null!, Outsider = null!, Warehouse = null!;
    internal ActorContext Reader => Procurement.Reader;
    internal static readonly SupplierOwner Owner = new(100, 101, 103);
    internal static readonly GrantScope Scope = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    internal static SupplierDetails Details => new("SYNTHETIC-SUBJECT-001", "PT Synthetic Supplier", "Synthetic stationery supplier", "synthetic-registration", "000001",
        "Synthetic supplier address 12, Jakarta", "Synthetic contact", "email", "supplier@example.invalid");
    internal const long PolicyId = 7001;
    internal const string TrustedChannel = "synthetic-approved-directory-entry-001", SourceReference = "synthetic-independent-source-001";
    internal SupplierMappingInput Mapping => new(ProcurementRequestTestData.Location, "supplier", "000001", "000012", Procurement.Sku.Id.Value, Procurement.Sku.Version,
        Procurement.Sku.CurrentDefinitionId!.Value.Value, Procurement.Sku.DefinitionVersion!.Value, Clock.UtcNow.AddDays(-1), Clock.UtcNow.AddDays(30));
    internal SupplierChangeInput Input => new(Owner, null, 0, PolicyId, 1, Details, [Mapping]);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Explicit synthetic supplier verification rehearsal", "supplier-test");
    internal BsonDocument Policy(long id = PolicyId, SupplierDetails? details = null) {
        details ??= Details;
        return new() { { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
            { "scope", new BsonDocument { { "organizationId", 100L }, { "shopId", 101L }, { "channelId", 103L } } },
            { "subjectReference", details.SubjectReference }, { "legalName", details.LegalName },
            { "registrationNamespace", details.RegistrationNamespace is null ? BsonNull.Value : details.RegistrationNamespace },
            { "registrationValue", details.RegistrationValue is null ? BsonNull.Value : details.RegistrationValue },
            { "method", "authenticated-record" }, { "trustedChannelReference", TrustedChannel }, { "operatingOwnerReference", "synthetic-operator-approval-owner" },
            { "approvalReference", "synthetic-business-policy-approval" }, { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime },
            { "expiresAt", Clock.UtcNow.AddDays(31).UtcDateTime }, { "maxObservationAgeSeconds", 900L }, { "allowReferenceOnly", true }, { "active", true } };
    }
    internal static async Task<SupplierTestData> CreateAsync(MongoFixture fixture) {
        var procurement = await ProcurementRequestTestData.CreateAsync(fixture);
        try {
            var data = new SupplierTestData(procurement);
            data.Maker = await data.Base.AddActorAsync(620, Scope, Permissions.SupplierChangePropose);
            data.Checker = await data.Base.AddActorAsync(621, Scope, Permissions.SupplierChangeApprove);
            data.Outsider = await data.Base.AddActorAsync(622, new(GrantScopeKind.FulfillmentShop, 100, ShopId: procurement.ForeignShopId, ChannelId: 103), Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove);
            data.Warehouse = await data.Base.AddActorAsync(623, ProcurementRequestTestData.Scope, Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Outsider.UserId, data.Warehouse.UserId);
            await data.Rows(Policies).InsertOneAsync(data.Policy()); return data;
        } catch { await procurement.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key, SupplierChangeInput? input = null) {
        input ??= Input; var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> ObserveAsync(BsonDocument proposed, string key, SupplierDetails? details = null) =>
        await Service.ObserveAsync(Checker, proposed["proposalId"].AsInt64, 1, details ?? Details, PolicyId, 1, TrustedChannel, SourceReference, true, Meta(key));
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposed, string key, long? observationId, SupplierDecision decision = SupplierDecision.Approve) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposed["proposalId"].AsInt64, decision, observationId);
        return await Service.DecideAsync(Checker, proposed["proposalId"].AsInt64, 1, decision, observationId, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument();
        foreach (var name in new[] { Heads, Proposals, Observations, Decisions, Versions, Namespaces, Policies }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence");
            state[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.supplier(?:[.:]|$)"))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task NoBusinessEffectsAsync() {
        await Procurement.NoBusinessEffectsAsync();
        foreach (var name in new[] { "receivingAccounts", "disbursements", "bankReceipts", "procurementRequests", "procurementRequestDecisions" })
            Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    public ValueTask DisposeAsync() => Procurement.DisposeAsync();
}

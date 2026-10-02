using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

// Bank policies and observations are explicit synthetic local inputs, never real bank verification.
// The supplier, catalog and organization history are produced by their actual command owners.
internal sealed class SupplierBankTestData(SupplierTestData suppliers, BsonDocument supplier) : IAsyncDisposable
{
    internal const string Heads = "supplierBankHeads", Proposals = "supplierBankProposals", Observations = "supplierBankObservations",
        Decisions = "supplierBankDecisions", Versions = "supplierBankVersions", Policies = "supplierBankPolicies", Prefix = "procurement.supplierbank.";
    internal const long PolicyId = 8101;
    internal const string TrustedChannel = "SYNTHETIC-APPROVED-BANK-DIRECTORY-001", SourceReference = "SYNTHETIC-INDEPENDENT-BANK-RECORD-001";
    internal SupplierTestData Suppliers { get; } = suppliers;
    internal BsonDocument Supplier { get; } = supplier;
    internal ProcurementRequestTestData Procurement => Suppliers.Procurement;
    internal StoreTestData Base => Procurement.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Procurement.Unit;
    internal SupplierBankService Service => new(Database, Unit, new(Database), Clock, Procurement.Catalog);
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, DetailsReader = null!, Outsider = null!, Warehouse = null!;
    internal static ReceivingAccountDetails Details => new("BNI", "000012345678", "PT Synthetic Supplier", "IDR");
    internal SupplierBankInput Input => new(SupplierTestData.Owner, Supplier["supplierId"].AsInt64, Supplier["versionId"].AsInt64,
        Supplier["version"].AsInt64, 0, PolicyId, 1, Details);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Explicit synthetic bank verification rehearsal", "supplier-bank-test");
    internal BsonDocument Policy() => new() { { "_id", PolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
        { "owner", new BsonDocument { { "organizationId", 100L }, { "shopId", 101L }, { "channelId", 103L } } },
        { "supplierId", Supplier["supplierId"] }, { "supplierVersionId", Supplier["versionId"] }, { "supplierVersion", Supplier["version"] },
        { "subjectReference", SupplierTestData.Details.SubjectReference }, { "bankCode", Details.BankCode }, { "bankName", "Synthetic bank directory entry" },
        { "currency", "IDR" }, { "method", "authenticated-bank-record" }, { "trustedChannelReference", TrustedChannel },
        { "authorizationReference", "SYNTHETIC-BANK-AUTHORIZATION" }, { "operatingOwnerReference", "SYNTHETIC-BANK-POLICY-OWNER" },
        { "approvalReference", "SYNTHETIC-BANK-POLICY-APPROVAL" }, { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime },
        { "expiresAt", Clock.UtcNow.AddDays(30).UtcDateTime }, { "maxObservationAgeSeconds", 900L }, { "allowReferenceOnly", true }, { "active", true } };
    internal static async Task<SupplierBankTestData> CreateAsync(MongoFixture fixture) {
        var suppliers = await SupplierTestData.CreateAsync(fixture);
        try {
            var proposed = await suppliers.ProposeAsync("bank-fixture-supplier");
            var observed = await suppliers.ObserveAsync(proposed, "bank-fixture-supplier-observed");
            var approved = await suppliers.DecideAsync(proposed, "bank-fixture-supplier-approved", observed["recordId"].AsInt64);
            return await PrepareAsync(suppliers, approved);
        } catch { await suppliers.DisposeAsync(); throw; }
    }
    internal static async Task<SupplierBankTestData> PrepareAsync(SupplierTestData suppliers, BsonDocument approved) {
        var data = new SupplierBankTestData(suppliers, approved);
        var scope = SupplierTestData.Scope;
        data.Maker = await data.Base.AddActorAsync(650, scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose);
        data.Checker = await data.Base.AddActorAsync(651, scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankApprove);
        data.Reader = await data.Base.AddActorAsync(652, scope, Permissions.SupplierBankRead);
        data.DetailsReader = await data.Base.AddActorAsync(653, scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead);
        data.Outsider = await data.Base.AddActorAsync(654, scope with { ShopId = data.Procurement.ForeignShopId }, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose, Permissions.SupplierBankApprove);
        data.Warehouse = await data.Base.AddActorAsync(655, ProcurementRequestTestData.Scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose, Permissions.SupplierBankApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.DetailsReader.UserId, data.Outsider.UserId, data.Warehouse.UserId);
        await data.Rows(Policies).InsertOneAsync(data.Policy()); return data;
    }
    internal async Task<BsonDocument> ProposeAsync(string key, SupplierBankInput? input = null) {
        input ??= Input; var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> ObserveAsync(BsonDocument proposed, string key, ReceivingAccountDetails? details = null) {
        details ??= Details; var id = proposed["proposalId"].AsInt64;
        var preview = await Service.PreviewObservationAsync(Checker, id, details, SourceReference);
        return await Service.ObserveAsync(Checker, id, 1, details, SourceReference, preview["binding"].AsString, true, Meta(key));
    }
    internal async Task<BsonDocument> DecideAsync(BsonDocument proposed, string key, long? observationId, SupplierBankDecision decision = SupplierBankDecision.Approve) {
        var id = proposed["proposalId"].AsInt64; var preview = await Service.PreviewDecisionAsync(Checker, id, decision, observationId);
        return await Service.DecideAsync(Checker, id, 1, decision, observationId, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state = new BsonDocument();
        foreach (var name in new[] { Heads, Proposals, Observations, Decisions, Versions, Policies }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); state[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { Prefix + "propose", Prefix + "observe", Prefix + "approve", Prefix + "reject" }))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task NoFinancialEffectsAsync() {
        foreach (var name in new[] { "goodsReceipts", "supplierInvoices", "stockBalances", "movements", "reservations", "journalEntries", "invoices", "paymentAllocations", "refundPayouts",
            "receivingAccounts", "disbursements", "bankReceipts", "supplierClaims", "procurementClaims" }) Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
    }
    public ValueTask DisposeAsync() => Suppliers.DisposeAsync();
}

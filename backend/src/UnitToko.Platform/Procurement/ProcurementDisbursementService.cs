using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementDisbursementService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly ProcurementFundingService funding;
    private readonly SupplierBankService beneficiaries;
    private readonly BankReceiptService banks;
    private readonly ProcurementDisbursementRecords records;
    public ProcurementDisbursementService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog,
        ProcurementFundingService funding, SupplierBankService beneficiaries, BankReceiptService banks) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.funding = funding; this.beneficiaries = beneficiaries; this.banks = banks;
        authorization = new(database, unit, clock); organizations = new(database, authorization, ids, clock); records = new(database, unit, clock, catalog, funding);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal ProcurementLocation Location = location;
        internal string Permission = permission;
        internal ResourceScope Resource => ResourceFor(Location);
        internal PersonAuthorityProof? People;
        internal DateTimeOffset? SessionExpiresAt;
        internal ProcurementFundingCapture? Funding;
        internal SupplierBankCapture? Beneficiary;
        internal RefundPayoutBankSource? Bank;
        internal BsonDocument? Policy, Source, Draft;
        internal DisbursementCapacityState? Capacity;
        internal ProcurementDisbursementProof? Proof;
        internal ProcurementDisbursementRecords.HistoricalSources? History;
    }
    private static ResourceScope ResourceFor(ProcurementLocation key) => new(FulfillmentOrganizationId: key.OrganizationId, ShopId: key.ShopId, WarehouseId: key.WarehouseId, ChannelId: key.ChannelId);
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context) => new[] { Permissions.ProcurementDisbursementRead, context.Permission }.Distinct().Select(permission => {
        var query = Query(context.Actor, context.Authority, permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
    }).ToArray();
    private Context ContextFor(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        ProcurementRequestRules.Location(location); var context = new Context(actor, authority, tx, location, permission); _ = Require(context); return context;
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128) || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            !ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128)) throw ProcurementDisbursementRules.Invalid();
    }
    internal async Task StorageAsync(CancellationToken ct) {
        foreach (var name in ProcurementDisbursementSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ProcurementDisbursementSchemas.Validator(name), ProcurementDisbursementSchemas.Keys(name), ct);
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task PeopleAsync(Context context, BsonDocument? source, BsonDocument? historical, CancellationToken ct) {
        var originals = new List<PersonEvidence>();
        if (source is not null) {
            var decision = source["funding"]["decision"]; originals.Add(decision["people"].AsBsonArray.Select(ProcurementFundingBson.Person).Single(person => person.UserId == decision["actorId"].AsInt64));
        }
        if (historical is not null) originals.AddRange(historical["people"].AsBsonArray.Select(ProcurementFundingBson.Person));
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. originals.Select(person => person.UserId).Append(context.Actor.UserId).Distinct().Order()], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) ||
            originals.Any(original => proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId)) throw new AuthorizationDeniedException("person_evidence_invalid");
        if (source is not null && !PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == context.Actor.UserId),
                proof.Evidence.Single(person => person.UserId == source["funding"]["decision"]["actorId"].AsInt64))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow); context.Funding?.RequireCurrent(); context.Beneficiary?.RequireCurrent(); context.Bank?.RequireCurrent();
        if (context.Policy is { } policy) ProcurementDisbursementRecords.PolicyAt(policy, Now);
        if (context.SessionExpiresAt is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
    }
    private async Task TouchAsync(Context context, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context);
        // Funding is fenced before beneficiary capture because both owners fence the same supplier identity.
        if (context.Beneficiary is { } beneficiary) await beneficiaries.FenceBeneficiaryAsync(database, context.Actor, context.Authority, context.Tx, beneficiary, ct);
        if (context.Bank is { } bank) await bank.FenceAsync(ct);
        if (context.Policy is { } policy) await TouchAsync(context, B.Policies, policy, ct);
        if (context.Capacity?.Head is { } head) await TouchAsync(context, B.Heads, head, ct);
        if (context.Draft is { } draft) await TouchAsync(context, B.Drafts, draft, ct);
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor, ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        if (context.Actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId, context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
            if (live is null) throw new AuthorizationDeniedException("stale_or_inactive_session");
            context.SessionExpiresAt = new DateTimeOffset(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task CaptureAsync(Context context, ProcurementDisbursementInput input, CancellationToken ct) {
        context.Funding = await funding.CaptureDisbursementAsync(database, context.Actor, context.Authority, context.Tx, input.FundingRequestId, input.FundingDecisionId, input.AuthorizationId, ct);
        await funding.FenceDisbursementAsync(database, context.Actor, context.Authority, context.Tx, context.Funding, ct);
        context.Beneficiary = await beneficiaries.CaptureBeneficiaryAsync(database, context.Actor, context.Authority, context.Tx, input.SupplierId, input.BeneficiaryVersionId, input.BeneficiaryVersion, ct);
        context.Bank = await banks.PayoutBankSourceAsync(context.Actor, context.Authority, context.Tx, input.PayingAccountId, input.PayingAccountVersionId, ct);
        context.Policy = await records.RowAsync(context.Tx, B.Policies, input.PolicyId, ct);
        var account = context.Bank.Account; var proposal = await new ReceivingAccountRecords(database).ProposalAsync(context.Tx, account["proposalId"].AsInt64, ct);
        context.Source = new() { { "funding", context.Funding.Source.DeepClone() }, { "beneficiary", context.Beneficiary.Source.DeepClone() }, { "payingBank", B.BankSnapshot(context.Bank) },
            { "payingAccount", B.AccountView(account, proposal["policy"]["bankName"].AsString) }, { "policy", B.PolicySnapshot(context.Policy) } };
        ProcurementDisbursementProof.RequireLinks(input, context.Source, Now);
        var earliest = new DateTimeOffset(new[] { context.Funding.Source["decision"]["recordedAt"].ToUniversalTime(), context.Beneficiary.Source["effectiveAt"].ToUniversalTime(),
            context.Bank.Account["effectiveAt"].ToUniversalTime(), context.Bank.FirstReceipt["recordedAt"].ToUniversalTime(), context.Bank.CashJournal["createdAt"].ToUniversalTime() }.Max());
        context.Proof = new(records, context.Tx, context.Source, earliest, () => Current(context));
        context.Capacity = await CapacityAsync(context.Tx, context.Funding.Source, ct, context.Proof); ProcurementDisbursementRecords.Review(input, context.Source, context.Capacity.View(), Now);
        await PeopleAsync(context, context.Source, null, ct); Current(context);
    }
}

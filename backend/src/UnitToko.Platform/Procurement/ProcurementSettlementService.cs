using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;
using D = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSettlementService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly ProcurementDisbursementService disbursements;
    private readonly BankReceiptService banks;
    private readonly ProcurementSettlementRecords records;
    public ProcurementSettlementService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog,
        ProcurementDisbursementService disbursements, BankReceiptService banks) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.disbursements = disbursements; this.banks = banks;
        authorization = new(database, unit, clock); organizations = new(database, authorization, ids, clock); records = new(database, clock, disbursements);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, bool write, bool privateSources) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal ProcurementLocation Location = location;
        internal bool Write = write, PrivateSources = privateSources;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Location.OrganizationId, ShopId: Location.ShopId, WarehouseId: Location.WarehouseId, ChannelId: Location.ChannelId);
        internal ProcurementDisbursementSettlementCapture? Source;
        internal ProcurementSettlementBankCapture? Bank;
        internal PersonAuthorityProof? People;
        internal BsonDocument? Policy, Accounting;
        internal DateTimeOffset? SessionExpiry;
        internal string? PostingDate;
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) { var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query; }
    private AuthorizationQuery[] Require(Context context) {
        var permissions = new List<string> { Permissions.ProcurementSettlementRead, Permissions.ProcurementDisbursementRead };
        if (context.Write) permissions.Add(Permissions.DisbursementVerify);
        if (context.PrivateSources) permissions.AddRange([Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead]);
        return permissions.Distinct().Select(permission => {
            var query = Query(context.Actor, context.Authority, permission);
            if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied"); return query;
        }).ToArray();
    }
    private Context ContextFor(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, bool write = false, bool privateSources = false) {
        ProcurementRequestRules.Location(location); var context = new Context(actor, authority, tx, location, write, privateSources); _ = Require(context); return context;
    }
    private void Current(Context context) {
        _ = Require(context); context.Source?.RequireCurrent(); context.Bank?.RequireCurrent(); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (context.Policy is { } policy) B.PolicyAt(policy, Now);
        if (context.PostingDate is { } date && date != InvoiceBson.Date(AccountingCalendar.BusinessDate(Now))) throw B.Changed();
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128) || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or ':' or '_' or '-')) ||
            !ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128)) throw ProcurementSettlementRules.Invalid();
    }
    private async Task StorageAsync(CancellationToken ct) {
        await disbursements.StorageAsync(ct); await banks.PayoutBankStorageAsync(ct);
        foreach (var name in ProcurementSettlementSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ProcurementSettlementSchemas.Validator(name), ProcurementSettlementSchemas.Keys(name), ct);
    }
    private async Task PeopleAsync(Context context, BsonDocument? settlement, CancellationToken ct) {
        var source = context.Source?.Record; var users = new List<long> { context.Actor.UserId };
        if (settlement is not null) users.AddRange(settlement["people"].AsBsonArray.Select(value => value["userId"].AsInt64));
        if (source is not null) users.Add(source["actorId"].AsInt64);
        var debit = context.Bank?.Debit ?? settlement?.GetValue("debit").AsBsonDocument;
        if (debit is not null) users.Add(debit["importedBy"].AsInt64);
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users.Distinct().Order()], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        if (source is not null) {
            var original = source["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == source["actorId"].AsInt64);
            if (proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId) throw new AuthorizationDeniedException("person_evidence_invalid");
        }
        if (debit is not null) {
            var imported = context.Bank?.Import ?? await new BankStatementRecords(database).ImportAsync(context.Tx, debit["importId"].AsInt64, ct);
            var original = ReceivingAccountBson.Person(imported["personAtImport"]);
            if (original.Verification != PersonVerification.Verified || proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId) throw new AuthorizationDeniedException("person_evidence_invalid");
            if (context.Write && !PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == context.Actor.UserId),
                proof.Evidence.Single(person => person.UserId == source!["actorId"].AsInt64), proof.Evidence.Single(person => person.UserId == debit["importedBy"].AsInt64))) throw new AuthorizationDeniedException("independent_person_required");
        }
        if (settlement is not null) foreach (var original in settlement["people"].AsBsonArray.Select(ReceivingAccountBson.Person)) {
            var actual = proof.Evidence.SingleOrDefault(person => person.UserId == original.UserId);
            if (actual is not null && actual.PersonId != original.PersonId) throw new AuthorizationDeniedException("person_evidence_invalid");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context);
        if (context.Source is { } source) await source.FenceAsync(ct);
        if (context.Bank is { } bank) await bank.FenceAsync(ct);
        if (context.Policy is { } policy) {
            if (policy["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Policies).UpdateOneAsync(context.Tx, policy, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            policy["fence"] = policy["fence"].AsInt64 + 1;
        }
        var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor, ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } people) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, people, ct);
        if (context.Actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId, context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct)
                ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
            context.SessionExpiry = new DateTimeOffset(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<Context> SourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, bool write, bool privateSources, CancellationToken ct) {
        var route = await Rows(D.Records).Find(tx, new BsonDocument("_id", id)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = ContextFor(actor, authority, tx, R.Location(route["location"]), write, privateSources);
        context.Source = await disbursements.CaptureSettlementAsync(actor, authority, tx, id, write ? Permissions.DisbursementVerify : Permissions.ProcurementDisbursementRead, ct);
        context.Source.RequireContext(disbursements, actor, authority, tx); return context;
    }
    private async Task<BsonDocument> AccountingAsync(Context context, BsonDocument policy, DateOnly date, CancellationToken ct) {
        var persistence = new AccountingPersistence(database, clock); var location = context.Location;
        var key = new OperationalBookKey(location.OrganizationId, location.ShopId, location.ChannelId, "IDR"); var book = await persistence.BookAsync(context.Tx, key, ct);
        ProcurementSettlementRecords.Equal(book["_id"], policy["bookId"]); ProcurementSettlementRecords.Equal(book["chartVersionId"], policy["chartVersionId"]);
        var chart = await Rows("accountingChartVersions").Find(context.Tx, new BsonDocument { { "_id", book["chartVersionId"] }, { "bookId", book["_id"] }, { "active", true } }).SingleOrDefaultAsync(ct) ?? throw B.Changed("procurement_settlement_accounting_changed");
        var snapshot = AccountingBson.ReadChart(chart); var month = AccountingCalendar.Month(date); var period = await persistence.PeriodAsync(context.Tx, book["_id"].AsInt64, month, ct);
        AccountingPersistence.RequireOpen(book, period, month);
        BsonDocument Mapping(string prefix) {
            var mapping = snapshot.Mappings.SingleOrDefault(value => value.MappingKey == policy[prefix + "Mapping"].AsString && value.Active) ?? throw B.Changed("procurement_settlement_accounting_changed");
            if (mapping.AccountId != policy[prefix + "AccountId"].AsInt64 || mapping.AccountCode != policy[prefix + "AccountCode"].AsString) throw B.Changed("procurement_settlement_accounting_changed");
            return new() { { "mappingKey", mapping.MappingKey }, { "accountId", mapping.AccountId }, { "accountCode", mapping.AccountCode } };
        }
        var advance = Mapping("supplierAdvance"); var cash = Mapping("cash");
        if (advance["accountId"] == cash["accountId"] || advance["mappingKey"] == cash["mappingKey"]) throw B.Broken();
        if (context.Bank is { } bank) ProcurementSettlementRecords.Equal(cash, bank.PayingBank.CashAccount);
        // Replacing an active chart must conflict with this transaction even when the
        // replacement preserves its identifier. No-op field update still takes a write lock.
        if ((await Rows("accountingChartVersions").UpdateOneAsync(context.Tx, chart, Builders<BsonDocument>.Update.Set("active", true), cancellationToken: ct)).MatchedCount != 1) throw B.Changed();
        await persistence.TouchBookAsync(context.Tx, book, ct);
        return new() { { "bookId", book["_id"] }, { "chartVersionId", chart["_id"] }, { "periodId", period["_id"] }, { "periodMonth", month }, { "supplierAdvance", advance }, { "cash", cash } };
    }
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, ProcurementSettlementInput input, CancellationToken ct) {
        var context = await SourceAsync(actor, authority, tx, id, true, true, ct); var record = context.Source!.Record;
        if (await records.ForDisbursementAsync(tx, id, ct) is not null) throw B.Changed("procurement_settlement_already_verified");
        context.Policy = await records.RowAsync(tx, B.Policies, input.PolicyId, ct); B.PolicyAt(context.Policy, Now);
        ProcurementSettlementRecords.Equal(context.Policy["revision"], input.PolicyRevision); ProcurementSettlementRecords.Equal(context.Policy["location"], record["location"]);
        foreach (var field in new[] { "payingAccountId", "payingAccountVersionId" }) ProcurementSettlementRecords.Equal(context.Policy[field], record["input"][field]);
        context.Bank = await banks.CaptureProcurementSettlementAsync(actor, authority, tx, record["input"]["payingAccountId"].AsInt64, record["input"]["payingAccountVersionId"].AsInt64, input.BankImportId, input.BankRowNumber, ct);
        context.Bank.RequireContext(banks, actor, authority, tx);
        ProcurementSettlementRecords.Review(record, input, context.Bank.Debit, context.Policy, Now);
        await PeopleAsync(context, null, ct); var date = AccountingCalendar.BusinessDate(Now); context.PostingDate = InvoiceBson.Date(date);
        context.Accounting = await AccountingAsync(context, context.Policy, date, ct); Current(context); return context;
    }
    private async Task AuditReadAsync(Context context, string purpose, BsonDocument selection, CancellationToken ct) => await Rows("auditEvents").InsertOneAsync(context.Tx,
        new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 }, { "kind", B.Prefix + "read" }, { "actorId", context.Actor.UserId },
            { "actorGrantVersion", context.Actor.GrantVersion }, { "location", R.Location(context.Location) }, { "purpose", purpose }, { "selection", selection }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
}

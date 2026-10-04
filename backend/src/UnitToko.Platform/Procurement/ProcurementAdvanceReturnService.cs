using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService(
    IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
    ProcurementSettlementService settlements, BankReceiptService bankReceipts,
    ProcurementSupplierPayableService payables, ProcurementRequestService requests)
{
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly ProcurementAdvanceReturnRecords records = new(database, settlements);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static ResourceScope Resource(ProcurementLocation location) => new(FulfillmentOrganizationId: location.OrganizationId,
        ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementAdvanceReturnInput input, string permission)
    {
        internal readonly ActorContext Actor = actor; internal readonly AuthorizationSnapshot Authority = authority;
        internal readonly IClientSessionHandle Tx = tx; internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly ProcurementAdvanceReturnInput Input = input; internal readonly string Permission = permission;
        internal BankReceiptSupplierReturnSource Bank = null!; internal BsonDocument Source = null!, Capacity = null!, Policy = null!, Accounting = null!;
        internal Dictionary<long, ProcurementPayableAdvanceSource> Advances = [];
        internal readonly ProcurementAdvanceReturnRecords.OwnerProofCache ReturnProofs = new(tx);
        internal readonly Dictionary<long, BsonDocument?> Heads = [];
        internal BsonDocument? Proposal; internal PersonAuthorityProof? People;
        internal ActorContext? MakerActor; internal AuthorizationSnapshot? MakerAuthority;
        internal DateTimeOffset? SessionExpiry;
    }
    private async Task StorageAsync(CancellationToken ct)
    {
        await settlements.RequirePayableSourceStorageAsync(ct); await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        await RequireReturnStorageAsync(database, ct);
        foreach (var name in ProcurementAdvanceReturnSchemas.ExtendedPayableCollections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementAdvanceReturnSchemas.CurrentPayableValidator(name),
                ProcurementSupplierPayableSchemas.Keys(name), ct);
    }
    internal static async Task RequireReturnStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in B.Collections) await InventoryRepository.RequireStorageAsync(database, name,
            ProcurementAdvanceReturnSchemas.Validator(name), ProcurementAdvanceReturnSchemas.Keys(name), ct);
        await InventoryRepository.RequireStorageAsync(database, PaymentVerificationBson.Parties,
            PaymentReceiptPurposeSchemas.Validator(), PaymentVerificationSchemas.Keys(PaymentVerificationBson.Parties), ct);
    }
    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        ProcurementRequestRules.Text(metadata.IdempotencyKey, 128); ProcurementRequestRules.Text(metadata.CorrelationId, 128);
        ProcurementRequestRules.Text(metadata.Reason, 1000);
        if (metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw B.Invalid();
    }
    private static void Binding(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw B.Invalid();
    }
    private static void Key(string value)
    {
        ProcurementRequestRules.Text(value, 128);
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw B.Invalid();
    }
    private AuthorizationQuery[] Queries(Context context, bool posting)
    {
        var names = new List<string> { Permissions.ProcurementRead, context.Permission };
        if (posting) names.Add(Permissions.JournalPost);
        return names.Distinct().Select(name => {
            var query = ScopePolicy.Compile(context.Actor, context.Authority, name, Now);
            AuthorizationStore.Require(query);
            if (!ScopePolicy.IsResourceActive(Resource(context.Input.Location), context.Authority.Directory) ||
                !query.Matches(Resource(context.Input.Location))) throw new AuthorizationDeniedException("scope_denied");
            return query;
        }).ToArray();
    }
    private void Current(Context context, bool posting)
    {
        _ = Queries(context, posting);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) ||
            context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        context.Bank?.RequireCurrent(); context.People?.RequireCurrent(context.Tx, Now);
        if (context.MakerActor is { } maker && context.MakerAuthority is { } snapshot) {
            var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.ProcurementAdvanceReturnPropose, Now);
            if (!query.Matches(Resource(context.Input.Location))) throw new AuthorizationDeniedException("original_maker_scope_changed");
        }
        if (context.Policy is not null && Now.UtcDateTime >= context.Policy["expiresAt"].ToUniversalTime())
            throw B.Changed("procurement_advance_return_policy_unavailable");
        if (context.SessionExpiry is { } expiry && Now >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
            actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, Now)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceAsync(Context context, CancellationToken ct, bool posting)
    {
        Current(context, posting);
        foreach (var advance in context.Advances.Values) await FenceImmutableAsync(context.Tx, ProcurementSettlementBson.Events, advance.Settlement, ct);
        if (context.Policy is { } policy) await TouchAsync(context.Tx, B.Policies, policy, ct);
        foreach (var head in context.Heads.Values) if (head is not null) await TouchAsync(context.Tx, P.AdvanceHeads, head, ct);
        var resource = Resource(context.Input.Location); var clauses = Queries(context, posting)
            .SelectMany(query => query.Clauses.Where(clause => clause.Matches(resource))).Distinct().ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context, posting);
    }
    private async Task FenceImmutableAsync(IClientSessionHandle tx, string table, BsonDocument row, CancellationToken ct)
    {
        if ((await Rows(table).DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw B.Changed();
        await Rows(table).InsertOneAsync(tx, row, cancellationToken: ct);
    }
    private async Task TouchAsync(IClientSessionHandle tx, string table, BsonDocument row, CancellationToken ct)
    {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(table).UpdateOneAsync(tx, row,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, ProcurementAdvanceReturnInput input, CancellationToken ct)
    {
        var row = await Rows(B.Policies).Find(tx, F.Eq("_id", input.PolicyId) & ProcurementAdvanceReturnSchemas.Validator(B.Policies))
            .SingleOrDefaultAsync(ct) ?? throw B.Changed("procurement_advance_return_policy_unavailable");
        if (row["revision"] != input.PolicyRevision || row["location"] != R.Location(input.Location) ||
            !row["active"].AsBoolean || row["currency"] != "IDR" || row["treatment"] != "whole_verified_supplier_advance_return" ||
            row["approvedAt"].ToUniversalTime() > Now.UtcDateTime || row["expiresAt"].ToUniversalTime() <= Now.UtcDateTime ||
            row["proposedBy"] == row["approvedBy"] || row["people"].AsBsonArray.Count != 2) throw B.Changed("procurement_advance_return_policy_unavailable");
        var evidence = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (!PersonIndependencePolicy.AreIndependent(evidence[0], evidence[1]) ||
            evidence.Select(value => value.UserId).ToHashSet().Count != 2 ||
            !evidence.Any(value => value.UserId == row["proposedBy"].AsInt64) ||
            !evidence.Any(value => value.UserId == row["approvedBy"].AsInt64)) throw B.Changed("procurement_advance_return_policy_unavailable");
        foreach (var field in new[] { "operatingOwnerReference", "approvalReference" }) ProcurementRequestRules.Text(row[field].AsString, 256);
        if (row["suspenseAccountId"] == row["supplierAdvanceAccountId"] || row["suspenseMapping"] == row["supplierAdvanceMapping"])
            throw B.Changed("procurement_advance_return_policy_unavailable");
        return row;
    }
    private async Task<BsonDocument> AccountingAsync(IClientSessionHandle tx, ProcurementAdvanceReturnInput input, BsonDocument policy, CancellationToken ct)
    {
        var key = new OperationalBookKey(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId, "IDR");
        if (await new AccountingBookSetupRecords(database).ApprovedAsync(tx, key, ct) is null)
            throw B.Changed("procurement_advance_return_accounting_unavailable");
        var persistence = new AccountingPersistence(database, clock); var book = await persistence.BookAsync(tx, key, ct);
        Equal(book["_id"], policy["bookId"]); Equal(book["chartVersionId"], policy["chartVersionId"]);
        var chart = await Rows("accountingChartVersions").Find(tx, F.Eq("_id", book["chartVersionId"]) & F.Eq("bookId", book["_id"]) & F.Eq("active", true))
            .SingleOrDefaultAsync(ct) ?? throw B.Changed("procurement_advance_return_accounting_unavailable");
        var mappings = AccountingBson.ReadChart(chart).Mappings; var month = AccountingCalendar.Month(input.PostingDate);
        var period = await persistence.PeriodAsync(tx, book["_id"].AsInt64, month, ct); AccountingPersistence.RequireOpen(book, period, month);
        BsonDocument Account(string role) {
            var selected = mappings.SingleOrDefault(value => value.Active && value.MappingKey == policy[role + "Mapping"].AsString)
                ?? throw B.Changed("procurement_advance_return_accounting_unavailable");
            if (selected.AccountId != policy[role + "AccountId"].AsInt64 || selected.AccountCode != policy[role + "AccountCode"].AsString)
                throw B.Changed("procurement_advance_return_accounting_unavailable");
            return new() { { "mappingKey", selected.MappingKey }, { "accountId", selected.AccountId }, { "accountCode", selected.AccountCode } };
        }
        var result = new BsonDocument { { "bookId", book["_id"] }, { "chartVersionId", chart["_id"] }, { "periodId", period["_id"] },
            { "periodMonth", month }, { "suspense", Account("suspense") }, { "supplierAdvance", Account("supplierAdvance") } };
        if ((await Rows("accountingChartVersions").DeleteOneAsync(tx, chart, cancellationToken: ct)).DeletedCount != 1) throw B.Changed();
        await Rows("accountingChartVersions").InsertOneAsync(tx, chart, cancellationToken: ct);
        await persistence.TouchBookAsync(tx, book, ct); return result;
    }
}

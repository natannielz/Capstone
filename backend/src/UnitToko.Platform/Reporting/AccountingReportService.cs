using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Reporting;

/// <summary>Audited, bounded reads of persisted journals in one explicitly authorized operational book.</summary>
public sealed partial class AccountingReportService
{
    private readonly IMongoDatabase database;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    public AccountingReportService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    {
        this.database = database; this.ids = ids; this.clock = clock;
        authorization = new(database, unit, clock); organizations = new(database, authorization, ids, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OperationalBookKey key, BsonDocument book, BsonDocument sources, DateTimeOffset deadline)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal OperationalBookKey Key { get; } = key;
        internal BsonDocument Book { get; } = book;
        internal BsonDocument Sources { get; } = sources;
        internal DateTimeOffset Deadline { get; } = deadline;
        internal DateTimeOffset? SessionDeadline { get; set; }
        internal ResourceScope Resource => ResourceFor(Key);
    }
    private static ResourceScope ResourceFor(OperationalBookKey key) => new(FulfillmentOrganizationId: key.OrganizationId, ShopId: key.ShopId, ChannelId: key.ChannelId);
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority)
    {
        var query = ScopePolicy.Compile(actor, authority, Permissions.ReportRead, clock.UtcNow); AuthorizationStore.Require(query);
        // A warehouse or buying grant cannot confer whole-book financial access by coincidence of IDs.
        query = query with { Clauses = [.. query.Clauses.Where(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)] };
        if (!query.IsAllowed) throw new AuthorizationDeniedException("scope_denied"); return query;
    }
    private void Current(Context context)
    {
        var query = Query(context.Actor, context.Authority);
        if (!query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        if (clock.UtcNow >= context.Deadline) throw new DomainValidationException("accounting_report_read_timed_out", "The report read timed out; reload and try again");
        if (context.SessionDeadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OperationalBookKey key, DateTimeOffset deadline, CancellationToken ct)
    {
        key.Validate(); var query = Query(actor, authority);
        if (!query.Matches(ResourceFor(key))) throw new AuthorizationDeniedException("scope_denied");
        var filter = new BsonDocument { { "canonicalKey", key.CanonicalKey }, { "active", true } };
        await ValidRowsAsync("accountingBooks", filter, AccountingSchemas.Validator("accountingBooks"), tx, ct);
        var book = await Rows("accountingBooks").Find(tx, filter).SingleOrDefaultAsync(ct) ??
            throw new DomainValidationException("accounting_report_book_unavailable", "Select an active configured book from the authorized list");
        if (AccountingBson.ReadKey(book["key"]) != key) throw AccountingReportRecords.Broken();
        var sources = new BsonDocument();
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, key.OrganizationId),
            ("shop", ScopeNodeKind.Shop, key.ShopId), ("channel", ScopeNodeKind.Channel, key.ChannelId) }) {
            var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), ct);
            foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node))
                if (authority.Directory.Find(node.Id, node.Kind) != node) throw AccountingReportRecords.Stale();
            sources.Add(name, source);
        }
        var context = new Context(actor, authority, tx, key, book, sources, deadline); Current(context); return context;
    }
    private async Task ValidRowsAsync(string name, BsonDocument filter, BsonDocument validator, IClientSessionHandle tx, CancellationToken ct)
    {
        if (await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { filter, new BsonDocument("$nor", new BsonArray { validator }) }))
            .Limit(1).AnyAsync(ct)) throw AccountingReportRecords.Broken();
    }
    private async Task<DateTimeOffset?> SessionDeadlineAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer,
            actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceAsync(Context context, CancellationToken ct)
    {
        Current(context);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw AccountingReportRecords.Broken();
        foreach (var name in new[] { "organization", "shop", "channel" })
            await organizations.FenceAddressProfileAsync(context.Tx, context.Sources[name].AsBsonDocument, true, ct);
        // All sanctioned posting/configuration writers touch this owner. The read serializes with their append/replacement.
        if (context.Book["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows("accountingBooks").UpdateOneAsync(context.Tx, context.Book,
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw AccountingReportRecords.Stale();
        var clauses = Query(context.Actor, context.Authority).Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.SessionDeadline = await SessionDeadlineAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private static AccountingReportLabel Label(BsonValue source) => new(new(source["profile"]["id"].AsInt64), source["profile"]["code"].AsString, source["profile"]["name"].AsString);
    private static AccountingReportBook Book(Context context) => new(new(context.Book["_id"].AsInt64),
        new(new(context.Key.OrganizationId), new(context.Key.ShopId), new(context.Key.ChannelId), context.Key.Currency),
        Label(context.Sources["organization"]), Label(context.Sources["shop"]), Label(context.Sources["channel"]));

    public async Task<AccountingReportBooks> BooksAsync(ActorContext actor, AccountingReportReadMetadata metadata, CancellationToken ct = default)
    {
        metadata.Validate(); using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var deadline = Now.AddSeconds(30); await AccountingReportRecords.RequireStorageAsync(database, bounded.Token);
        Action? release = null;
        var result = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var clauses = Query(actor, authority).Clauses;
            var filter = new BsonDocument { { "active", true }, { "$or", new BsonArray(clauses.Select(clause => new BsonDocument {
                { "key.organizationId", clause.Scope.OrganizationId!.Value }, { "key.shopId", clause.Scope.ShopId!.Value },
                { "key.channelId", clause.Scope.ChannelId!.Value } }).Distinct()) } };
            await ValidRowsAsync("accountingBooks", filter, AccountingSchemas.Validator("accountingBooks"), tx, token);
            var books = await Rows("accountingBooks").Find(tx, filter).Sort(new BsonDocument("canonicalKey", 1)).Limit(AccountingReportRules.MaximumBooks + 1).ToListAsync(token);
            if (books.Count > AccountingReportRules.MaximumBooks) throw AccountingReportRules.TooLarge();
            var contexts = new List<Context>(); var items = new List<AccountingReportBook>(); var proof = new BsonArray();
            foreach (var row in books) {
                var context = await ContextAsync(actor, authority, tx, AccountingBson.ReadKey(row["key"]), deadline, token);
                await CurrentChartAsync(context, token); contexts.Add(context); items.Add(Book(context));
                proof.Add(new BsonDocument { { "book", AccountingReportRecords.StableBook(context.Book) }, { "sources", context.Sources } });
            }
            var id = (await ids.NextAsync(AccountingReportRecords.Events, token)).Value;
            var audit = Audit(id, actor, metadata, AccountingReportRecords.BooksOperation, Now, null, null, null,
                AccountingReportRecords.Hash(proof), [], null, null, null, 0, 0, 0, items.Count);
            await Rows(AccountingReportRecords.Events).InsertOneAsync(tx, audit, cancellationToken: token);
            foreach (var context in contexts) await FenceAsync(context, token);
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            var sessionDeadline = await SessionDeadlineAsync(actor, tx, token);
            release = () => {
                _ = Query(actor, authority); foreach (var context in contexts) Current(context);
                if (clock.UtcNow >= deadline) throw new DomainValidationException("accounting_report_read_timed_out", "Reload the report and try again");
                if (sessionDeadline is { } expires && clock.UtcNow >= expires) throw new AuthorizationDeniedException("stale_or_inactive_session");
            };
            release(); token.ThrowIfCancellationRequested(); return new AccountingReportBooks(new(id), items.AsReadOnly());
        }, bounded.Token);
        release!(); bounded.Token.ThrowIfCancellationRequested(); return result;
    }

    public async Task<AccountingReportSnapshot> ReadAsync(ActorContext actor, OperationalBookKey key, string endDate,
        AccountingReportReadMetadata metadata, CancellationToken ct = default)
    {
        if (key is null) throw new DomainValidationException("invalid_accounting_report_key", "Select an explicit authorized operational book");
        key.Validate(); _ = AccountingReportRules.EndDate(endDate); metadata.Validate();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var deadline = Now.AddSeconds(30); await AccountingReportRecords.RequireStorageAsync(database, bounded.Token); Action? release = null;
        var result = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, key, deadline, token);
            var filter = new BsonDocument { { "bookId", context.Book["_id"] }, { "businessDate", new BsonDocument("$lte", endDate) } };
            await ValidRowsAsync("journalEntries", filter, AccountingSchemas.Validator("journalEntries"), tx, token);
            var journals = await Rows("journalEntries").Find(tx, filter).Sort(new BsonDocument { { "businessDate", 1 }, { "_id", 1 } })
                .Limit(AccountingReportRules.MaximumJournals + 1).ToListAsync(token);
            if (journals.Count > AccountingReportRules.MaximumJournals) throw AccountingReportRules.TooLarge();
            var capture = await CaptureAsync(context, endDate, journals, token);
            var id = (await ids.NextAsync(AccountingReportRecords.Events, token)).Value;
            var captured = Now; var expires = captured.Add(AccountingReportRules.SnapshotLifetime);
            var snapshot = capture.Snapshot(new(id), captured, expires); _ = AccountingReportCsvWriter.Create(snapshot); // Page and export share one finite contract.
            var audit = Audit(id, actor, metadata, AccountingReportRecords.ReadOperation, captured, context, endDate, expires,
                capture.Hash, capture.Selection, capture.ChartSetHash, null, capture.BookHash, journals.Count,
                snapshot.Journals.Sum(journal => journal.Lines.Count), snapshot.Accounts.Count, snapshot.Journals.Sum(journal => journal.Lines.Count));
            await Rows(AccountingReportRecords.Events).InsertOneAsync(tx, audit, cancellationToken: token);
            await FenceAsync(context, token); release = () => Current(context); release(); token.ThrowIfCancellationRequested(); return snapshot;
        }, bounded.Token);
        release!(); bounded.Token.ThrowIfCancellationRequested(); return result;
    }

    public async Task<AccountingReportCsv> ExportAsync(ActorContext actor, long snapshotReadId, string snapshotHash,
        AccountingReportReadMetadata metadata, CancellationToken ct = default)
    {
        _ = new LongId(snapshotReadId); AccountingReportRules.Binding(snapshotHash); metadata.Validate();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var deadline = Now.AddSeconds(30); await AccountingReportRecords.RequireStorageAsync(database, bounded.Token); Action? release = null;
        var result = await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Query(actor, authority);
            var audit = await Rows(AccountingReportRecords.Events).Find(tx, new BsonDocument {
                { "_id", snapshotReadId }, { "actorId", actor.UserId }, { "operation", AccountingReportRecords.ReadOperation } }).SingleOrDefaultAsync(token);
            if (audit is null) throw new DomainValidationException("accounting_report_snapshot_unavailable", "Reload your own report before exporting");
            await ValidRowsAsync(AccountingReportRecords.Events, new BsonDocument("_id", snapshotReadId), AccountingReportSchemas.Validator(), tx, token);
            if (audit["eventHash"].AsString != AccountingReportRecords.EventHash(audit)) throw AccountingReportRecords.Broken();
            var expires = new DateTimeOffset(audit["expiresAt"].ToUniversalTime());
            if (clock.UtcNow >= expires || audit["snapshotHash"] != snapshotHash) throw AccountingReportRecords.Stale();
            var key = AccountingBson.ReadKey(audit["key"]); var endDate = audit["endDate"].AsString; _ = AccountingReportRules.EndDate(endDate);
            var context = await ContextAsync(actor, authority, tx, key, deadline, token);
            var selection = audit["selection"].AsBsonArray;
            var filter = new BsonDocument { { "bookId", context.Book["_id"] }, { "_id", new BsonDocument("$in", new BsonArray(selection.Select(item => item["journalId"]))) } };
            await ValidRowsAsync("journalEntries", filter, AccountingSchemas.Validator("journalEntries"), tx, token);
            var journals = await Rows("journalEntries").Find(tx, filter).Sort(new BsonDocument { { "businessDate", 1 }, { "_id", 1 } })
                .Limit(AccountingReportRules.MaximumJournals + 1).ToListAsync(token);
            if (journals.Count != audit["journalCount"].AsInt32 || journals.Count != selection.Count ||
                journals.Where((row, index) => row["_id"] != selection[index]["journalId"] ||
                    AccountingReportRecords.Hash(row) != selection[index]["contentHash"].AsString).Any()) throw AccountingReportRecords.Broken();
            var capture = await CaptureAsync(context, endDate, journals, token);
            if (context.Book["_id"] != audit["bookId"] || capture.Hash != snapshotHash || capture.BookHash != audit["bookHash"] ||
                capture.ChartSetHash != audit["chartSetHash"] || AccountingReportRecords.Hash(context.Sources) != audit["sourcesHash"]) throw AccountingReportRecords.Stale();
            var snapshot = capture.Snapshot(new(snapshotReadId), new(audit["recordedAt"].ToUniversalTime()), expires);
            if (snapshot.Accounts.Count != audit["accountCount"].AsInt32 || snapshot.Journals.Sum(row => row.Lines.Count) != audit["lineCount"].AsInt32)
                throw AccountingReportRecords.Broken();
            var bytes = AccountingReportCsvWriter.Create(snapshot);
            var id = (await ids.NextAsync(AccountingReportRecords.Events, token)).Value;
            var exportedAt = Now; if (exportedAt >= expires) throw AccountingReportRecords.Stale();
            var exported = Audit(id, actor, metadata, AccountingReportRecords.ExportOperation, exportedAt, context, endDate, expires,
                capture.Hash, capture.Selection, capture.ChartSetHash, snapshotReadId, capture.BookHash, journals.Count,
                audit["lineCount"].AsInt32, audit["accountCount"].AsInt32, audit["lineCount"].AsInt32);
            await Rows(AccountingReportRecords.Events).InsertOneAsync(tx, exported, cancellationToken: token);
            await FenceAsync(context, token); release = () => { Current(context); if (clock.UtcNow >= expires) throw AccountingReportRecords.Stale(); };
            release(); token.ThrowIfCancellationRequested();
            return new AccountingReportCsv(new(id), new(snapshotReadId), snapshotHash, $"unit-toko-neraca-jurnal-{endDate}-{new LongId(snapshotReadId)}.csv", bytes);
        }, bounded.Token);
        release!(); bounded.Token.ThrowIfCancellationRequested(); return result;
    }

    private static BsonDocument Audit(long id, ActorContext actor, AccountingReportReadMetadata metadata, string operation,
        DateTimeOffset recordedAt, Context? context, string? endDate, DateTimeOffset? expiresAt, string hash, BsonArray selection,
        string? chartSetHash, long? snapshotReadId, string? bookHash, int journals, int lines, int accounts, int resultCount)
    {
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "permission", Permissions.ReportRead }, { "operation", operation }, { "purpose", metadata.Purpose }, { "correlationId", metadata.CorrelationId },
            { "recordedAt", recordedAt.UtcDateTime }, { "expiresAt", expiresAt is { } expires ? new BsonDateTime(expires.UtcDateTime) : BsonNull.Value },
            { "snapshotReadId", AccountingBson.Optional(snapshotReadId) }, { "bookId", context is null ? BsonNull.Value : context.Book["_id"] },
            { "key", context is null ? BsonNull.Value : AccountingBson.Key(context.Key) }, { "endDate", endDate is null ? BsonNull.Value : new BsonString(endDate) },
            { "bookHash", bookHash is null ? BsonNull.Value : new BsonString(bookHash) },
            { "sourcesHash", context is null ? BsonNull.Value : new BsonString(AccountingReportRecords.Hash(context.Sources)) },
            { "chartSetHash", chartSetHash is null ? BsonNull.Value : new BsonString(chartSetHash) }, { "snapshotHash", hash },
            { "selection", selection }, { "journalCount", journals }, { "lineCount", lines }, { "accountCount", accounts }, { "resultCount", resultCount } };
        row.Add("eventHash", AccountingReportRecords.EventHash(row)); return row;
    }
}

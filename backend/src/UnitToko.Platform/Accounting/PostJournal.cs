using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Accounting;
/// <summary>
/// Joins an already-authorized owning command's active transaction. Duplicate source is ALWAYS an abort,
/// including identical content: returning success here could commit an earlier duplicate stock mutation.
/// Replay the entire command via its outer authorization/idempotency receipt before any business effect.
/// No adapter or accounting workflow is activated by constructing this boundary.
/// </summary>
public sealed class PostJournal
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IReadOnlyDictionary<string, IAccountingSourceReader> readers;
    private readonly AccountingPersistence persistence;
    private readonly IdAllocator ids;
    public PostJournal(IMongoDatabase database, IClock clock, IEnumerable<IAccountingSourceReader> readers)
    {
        this.database = database;
        this.clock = clock;
        persistence = new(database, clock);
        ids = new(database);
        var configured = readers.ToArray();
        foreach (var reader in configured)
        {
            AccountingValidation.Code(reader.Namespace);
            if (!ReferenceEquals(reader.Database.Client, database.Client) || !reader.Database.DatabaseNamespace.Equals(database.DatabaseNamespace))
                throw new ArgumentException("Source adapter must own the same application database and client");
        }

        this.readers = configured.ToDictionary(reader => reader.Namespace, StringComparer.Ordinal);
    }

    private IAccountingSourceReader Reader(IClientSessionHandle session, ActorContext actor, JournalSource source, long revision, AccountingWriteMetadata metadata)
    {
        AccountingPersistence.RequireSession(database, session);
        AccountingPersistence.ValidateActor(actor, metadata);
        if (source is null)
            throw AccountingValidation.Invalid("invalid_source");
        source.Validate();
        AccountingValidation.Positive(revision);
        if (!readers.TryGetValue(source.Namespace, out var reader))
            throw AccountingValidation.Invalid("accounting_source_unavailable");
        return reader;
    }

    public async Task<JournalReceipt> PostAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, AccountingWriteMetadata metadata, CancellationToken token = default)
    {
        try
        {
            var reader = Reader(session, actor, source, expectedRevision, metadata);
            var snapshot = await reader.ReadAndFenceAsync(session, actor, source, expectedRevision, token);
            if (snapshot is null || snapshot.Source != source || snapshot.Revision != expectedRevision)
                throw AccountingValidation.Invalid("accounting_source_mismatch");
            var book = await persistence.BookAsync(session, snapshot.Book, token);
            var chart = await persistence.Collection("accountingChartVersions").Find(session, new BsonDocument { { "_id", book["chartVersionId"] }, { "bookId", book["_id"] }, { "active", true } }).SingleOrDefaultAsync(token);
            if (chart is null)
                throw AccountingValidation.Invalid("accounting_chart_unavailable");
            var draft = JournalDraft.Create(snapshot, AccountingBson.ReadChart(chart));
            return await PersistAsync(session, actor, book, draft, metadata, token);
        }
        catch
        {
            await AccountingPersistence.AbortAsync(session);
            throw;
        }
    }

    public async Task<JournalReceipt> ReverseAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, AccountingWriteMetadata metadata, CancellationToken token = default)
    {
        try
        {
            var reader = Reader(session, actor, source, expectedRevision, metadata);
            var snapshot = await reader.ReadReversalAndFenceAsync(session, actor, source, expectedRevision, token);
            if (snapshot is null || snapshot.Source != source || snapshot.Revision != expectedRevision || snapshot.Book is null || snapshot.Dimensions is null)
                throw AccountingValidation.Invalid("accounting_source_mismatch");
            AccountingValidation.Positive(snapshot.OriginalJournalId);
            snapshot.Dimensions.Validate(snapshot.Book);
            var book = await persistence.BookAsync(session, snapshot.Book, token);
            var original = await persistence.Collection("journalEntries").Find(session, new BsonDocument { { "_id", snapshot.OriginalJournalId }, { "bookId", book["_id"] } }).SingleOrDefaultAsync(token);
            if (original is null || !original["reversalOf"].IsBsonNull)
                throw AccountingValidation.Invalid("reversal_original_unavailable");
            var content = AccountingBson.ReadDraft(original);
            if (content.Dimensions != snapshot.Dimensions)
                throw AccountingValidation.Invalid("source_scope_mismatch");
            var draft = JournalDraft.Reverse(source, expectedRevision, snapshot.Book, content.Dimensions, snapshot.Date, snapshot.OriginalJournalId, content.ChartId, content.ChartVersion, content.Lines);
            return await PersistAsync(session, actor, book, draft, metadata, token);
        }
        catch
        {
            await AccountingPersistence.AbortAsync(session);
            throw;
        }
    }

    private async Task<JournalReceipt> PersistAsync(IClientSessionHandle session, ActorContext actor, BsonDocument book, JournalDraft draft, AccountingWriteMetadata metadata, CancellationToken token)
    {
        var entries = persistence.Collection("journalEntries");
        var duplicate = await entries.Find(session, new BsonDocument("sourceEvent", draft.Source.EventKey)).SingleOrDefaultAsync(token);
        if (duplicate is not null)
        {
            _ = AccountingBson.ReadDraft(duplicate);
            throw new PersistenceConflictException(duplicate["fingerprint"] == draft.Fingerprint ? "journal_source_duplicate" : "journal_source_conflict", "Accounting source is already posted; reconcile the whole command before retrying");
        }

        if (draft.ReversalOf.HasValue && await entries.Find(session, new BsonDocument("reversalOf", draft.ReversalOf.Value)).AnyAsync(token))
            throw new PersistenceConflictException("journal_already_reversed", "Original entry already has a reversal");
        var month = AccountingCalendar.Month(draft.Date);
        var period = await persistence.PeriodAsync(session, book["_id"].AsInt64, month, token);
        AccountingPersistence.RequireOpen(book, period, month);
        await persistence.TouchBookAsync(session, book, token);
        // Monotonic allocator reservation is the only nontransactional exception; aborted/retried work leaves gaps.
        var id = (await ids.NextAsync("journalEntries", token)).Value;
        await entries.InsertOneAsync(session, AccountingBson.Journal(id, book["_id"].AsInt64, draft, actor, metadata, clock.UtcNow), cancellationToken: token);
        await persistence.InvalidateAsync(session, book["_id"].AsInt64, month, actor, metadata, token);
        await persistence.AuditAsync(session, draft.ReversalOf.HasValue ? "journal.reversed" : "journal.posted", $"accounting:journal:{id}", book["_id"].AsInt64, id, null, draft.Fingerprint, actor, metadata, token);
        return new(id, draft.Source.EventKey, draft.Fingerprint);
    }
}

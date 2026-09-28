using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Accounting;
public sealed record AccountingWriteMetadata(string Reason, string CorrelationId);
public sealed record JournalReceipt(long Id, string SourceEvent, string Fingerprint);
public sealed record ReversalSourceSnapshot(JournalSource Source, long Revision, OperationalBookKey Book, JournalDimensions Dimensions, DateOnly Date, long OriginalJournalId);
/// <summary>Trusted owning-module adapter, never HTTP input. Each method must load and conditionally WRITE the source revision in this session. A snapshot read alone is insufficient.</summary>
public interface IAccountingSourceReader
{
    string Namespace { get; }

    IMongoDatabase Database { get; }

    Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken token);
    Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle session, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken token);
}

internal sealed record AccountingStorageSnapshot(long BookId, long PeriodId, long DataRevision, string Hash, decimal Debit, decimal Credit, int JournalCount);

using System.Diagnostics;

namespace UnitToko.Platform.Storage;

/// <summary>Loaded by the service only after a known successful attempt/quota commit.</summary>
internal sealed record StorageAttempt
{
    public string StoreId { get; }
    public long ReservationId { get; }
    public long AttemptId { get; }
    public long Revision { get; }
    public string StageToken { get; }
    public string Generation { get; }
    public StorageAttempt(string storeId, long reservationId, long attemptId, long revision, string stageToken, string generation)
    {
        StorageValidation.Token(storeId); StorageValidation.Token(stageToken); StorageValidation.Token(generation);
        if (reservationId <= 0 || attemptId <= 0 || revision <= 0) throw new ArgumentException("Invalid durable storage identity.");
        StoreId = storeId; ReservationId = reservationId; AttemptId = attemptId;
        Revision = revision; StageToken = stageToken; Generation = generation;
    }
}
internal sealed record StorageContent
{
    public long Length { get; }
    public string Sha256 { get; }
    public StorageContent(long length, string sha256)
    {
        if (length <= 0) throw new ArgumentException("Content must be nonempty.");
        StorageValidation.Token(sha256); Length = length; Sha256 = sha256;
    }
}
/// <summary>Caller loads the frozen tuple from a known committed pending reservation; this type does not prove a commit.</summary>
internal sealed record ExpectedPublication
{
    public StorageAttempt Attempt { get; }
    public long ContentRevision { get; }
    public StorageContent Content { get; }
    public ExpectedPublication(StorageAttempt attempt, long contentRevision, StorageContent content)
    {
        ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(content);
        if (contentRevision <= 0) throw new ArgumentException("Invalid committed content revision.");
        Attempt = attempt; ContentRevision = contentRevision; Content = content;
    }
}
internal sealed record StorageIoBudget
{
    public long MaximumBytes { get; }
    public TimeSpan Duration { get; }
    public int BufferBytes { get; }
    public StorageIoBudget(long maximumBytes, TimeSpan duration, int bufferBytes)
    {
        if (maximumBytes <= 0 || duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1 ||
            bufferBytes is <= 0 or > 1_048_576)
            throw new ArgumentException("Storage needs explicit finite byte, time and buffer budgets.");
        MaximumBytes = maximumBytes; Duration = duration; BufferBytes = bufferBytes;
    }
}
internal sealed record SealedStage
{
    public StorageAttempt Attempt { get; }
    public StorageContent Content { get; }
    internal SealedStage(StorageAttempt attempt, StorageContent content) { Attempt = attempt; Content = content; }
}
internal abstract record StageOutcome
{
    internal sealed record Sealed(SealedStage Stage) : StageOutcome;
    internal sealed record ExistingAttempt : StageOutcome;
    internal sealed record Rejected(string Reason) : StageOutcome;
    internal sealed record Interrupted(string Reason, Exception? Failure = null) : StageOutcome;
    internal sealed record Unavailable(string Reason, Exception? Failure = null) : StageOutcome;
}
internal enum InspectionState { Absent, Matching, Incomplete, Conflict, Unknown, Unavailable }
internal sealed record StorageInspection(InspectionState State, string? Reason = null, Exception? Failure = null);
internal enum PublishState { Published, ExistingIdentical, Incomplete, Conflict, Unknown, Unavailable }
internal sealed record PublishOutcome(PublishState State, string? Reason = null, Exception? Failure = null);
internal abstract record OpenOutcome
{
    internal sealed record Opened(VerifiedOriginalHandle Handle) : OpenOutcome;
    internal sealed record Rejected(StorageInspection Inspection) : OpenOutcome;
}
internal sealed record StoreActivation(IPrivateEvidenceStore? Store, string Reason);
internal interface IPrivateEvidenceStore
{
    ValueTask<StageOutcome> StageAsync(StorageAttempt attempt, Stream source, StorageIoBudget budget, CancellationToken cancellationToken);
    ValueTask<StorageInspection> InspectStageAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken);
    ValueTask<PublishOutcome> PublishAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken);
    ValueTask<StorageInspection> InspectOriginalAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken);
    ValueTask<OpenOutcome> OpenVerifiedAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken);
}
/// <summary>No files, source reads, scanner verdicts or production activation.</summary>
internal sealed class UnavailablePrivateEvidenceStore : IPrivateEvidenceStore
{
    public ValueTask<StageOutcome> StageAsync(StorageAttempt attempt, Stream source, StorageIoBudget budget, CancellationToken cancellationToken) =>
        ValueTask.FromResult<StageOutcome>(new StageOutcome.Unavailable("storage_unavailable"));
    public ValueTask<StorageInspection> InspectStageAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StorageInspection(InspectionState.Unavailable, "storage_unavailable"));
    public ValueTask<PublishOutcome> PublishAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PublishOutcome(PublishState.Unavailable, "storage_unavailable"));
    public ValueTask<StorageInspection> InspectOriginalAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StorageInspection(InspectionState.Unavailable, "storage_unavailable"));
    public ValueTask<OpenOutcome> OpenVerifiedAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        ValueTask.FromResult<OpenOutcome>(new OpenOutcome.Rejected(new StorageInspection(InspectionState.Unavailable, "storage_unavailable")));
}
internal static class StorageValidation
{
    internal static void Token(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Storage tokens must be canonical lowercase hexadecimal values.");
    }
}
internal sealed class StorageOperation : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly long started = Stopwatch.GetTimestamp();
    internal StorageIoBudget Budget { get; }
    internal CancellationToken Token => cancellation.Token;
    internal StorageOperation(StorageIoBudget budget, CancellationToken cancellationToken)
    {
        Budget = budget; cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(budget.Duration);
    }
    internal void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= Budget.Duration)
            throw new OperationCanceledException("Storage deadline elapsed.", Token);
    }
    public void Dispose() => cancellation.Dispose();
}
internal sealed class StorageUnavailableException(string reason) : Exception(reason);
internal sealed class StorageLimitException : Exception;


internal sealed class StorageSourceException(Exception cause) : Exception("External source failed.", cause);

using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;

namespace UnitToko.Platform.Storage;

/// <summary>Local protocol only: no automatic production registration, deletion, overwrite or authority.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLocalEvidenceStore : IPrivateEvidenceStore
{
    private readonly WindowsPrivateStorageRoot root;
    private readonly string storeId;
    private WindowsLocalEvidenceStore(WindowsPrivateStorageRoot root, string storeId) { this.root = root; this.storeId = storeId; }
    internal static StoreActivation TryOpen(string root, string storeId, IReadOnlyCollection<string> trustedOperatorSids)
    {
        if (!OperatingSystem.IsWindows()) return new(null, "storage_platform_unsupported");
        try
        {
            StorageValidation.Token(storeId);
            return new(new WindowsLocalEvidenceStore(new WindowsPrivateStorageRoot(root, trustedOperatorSids), storeId), "storage_local_observed_only");
        }
        catch (Exception exception) when (IsStorageFailure(exception) || exception is ArgumentException) { return new(null, Reason(exception)); }
    }

    public async ValueTask<StageOutcome> StageAsync(StorageAttempt attempt, Stream source, StorageIoBudget budget, CancellationToken cancellationToken)
    {
        if (attempt is null || source is null || budget is null || attempt.StoreId != storeId || !source.CanRead)
            return new StageOutcome.Rejected("storage_input_invalid");
        if (cancellationToken.IsCancellationRequested) return new StageOutcome.Rejected("storage_cancelled");
        using var operation = new StorageOperation(budget, cancellationToken);
        var createAttempted = false;
        try
        {
            CheckNamespace(operation);
            var partPath = StagePath(attempt, ".part");
            var readyPath = StagePath(attempt, ".ready");
            var partExists = root.ValidateLeaf(partPath);
            var readyExists = root.ValidateLeaf(readyPath);
            if (partExists || readyExists) return new StageOutcome.ExistingAttempt();
            StorageContent content;
            createAttempted = true;
            FileStream part;
            try { part = OpenFile(partPath, FileMode.CreateNew, FileAccess.Write, operation); }
            catch (IOException exception) when (IsCollision(exception)) { return new StageOutcome.ExistingAttempt(); }
            await using (part)
            {
                content = await CopyAndHashAsync(source, part, budget.MaximumBytes, operation, externalSource: true);
                Flush(part, operation);
            }
            await using (var input = OpenFile(partPath, FileMode.Open, FileAccess.Read, operation))
            {
                if ((await VerifyAsync(input, content, operation)).State != InspectionState.Matching)
                    return new StageOutcome.Interrupted("storage_stage_integrity");
                await using var ready = OpenFile(readyPath, FileMode.CreateNew, FileAccess.Write, operation);
                var copied = await CopyAndHashAsync(input, ready, content.Length, operation);
                if (copied != content) return new StageOutcome.Interrupted("storage_stage_integrity");
                Flush(ready, operation);
            }
            await using (var ready = OpenFile(readyPath, FileMode.Open, FileAccess.Read, operation))
            {
                if ((await VerifyAsync(ready, content, operation)).State != InspectionState.Matching)
                    return new StageOutcome.Interrupted("storage_stage_integrity");
            }
            operation.Check();
            return new StageOutcome.Sealed(new SealedStage(attempt, content));
        }
        catch (StorageUnavailableException exception) { return new StageOutcome.Unavailable(Reason(exception), exception); }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return createAttempted ? new StageOutcome.Interrupted(Reason(exception), exception) : new StageOutcome.Unavailable(Reason(exception), exception);
        }
    }

    public ValueTask<StorageInspection> InspectStageAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        InspectAsync(expected, budget, cancellationToken, stage: true);
    public ValueTask<StorageInspection> InspectOriginalAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken) =>
        InspectAsync(expected, budget, cancellationToken, stage: false);
    private async ValueTask<StorageInspection> InspectAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken, bool stage)
    {
        if (!Valid(expected, budget)) return new(InspectionState.Unavailable, "storage_input_invalid");
        using var operation = new StorageOperation(budget, cancellationToken);
        try
        {
            CheckNamespace(operation);
            var path = stage ? StagePath(expected.Attempt, ".ready") : OriginalPath(expected.Attempt);
            if (!root.ValidateLeaf(path)) return new(InspectionState.Absent);
            await using var file = OpenFile(path, FileMode.Open, FileAccess.Read, operation);
            return await VerifyAsync(file, expected.Content, operation);
        }
        catch (FileNotFoundException) { return new(InspectionState.Absent); }
        catch (StorageUnavailableException exception) { return new(InspectionState.Unavailable, Reason(exception), exception); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new(InspectionState.Unknown, Reason(exception), exception); }
    }

    public async ValueTask<PublishOutcome> PublishAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken)
    {
        if (!Valid(expected, budget)) return new(PublishState.Unavailable, "storage_input_invalid");
        using var operation = new StorageOperation(budget, cancellationToken);
        try
        {
            CheckNamespace(operation);
            var destination = OriginalPath(expected.Attempt);
            if (root.ValidateLeaf(destination)) return await RecoverPendingAsync(destination, expected.Content, operation);
            var readyPath = StagePath(expected.Attempt, ".ready");
            if (!root.ValidateLeaf(readyPath)) return new(PublishState.Unavailable, "storage_stage_absent");
            await using (var ready = OpenFile(readyPath, FileMode.Open, FileAccess.Read, operation))
            {
                var stageInspection = await VerifyAsync(ready, expected.Content, operation);
                if (stageInspection.State != InspectionState.Matching) return FromInspection(stageInspection);
                FileStream destinationFile;
                try { destinationFile = OpenFile(destination, FileMode.CreateNew, FileAccess.Write, operation); }
                catch (IOException exception) when (IsCollision(exception)) { return await RecoverPendingAsync(destination, expected.Content, operation); }
                await using (destinationFile)
                {
                    var copied = await CopyAndHashAsync(ready, destinationFile, expected.Content.Length, operation);
                    if (copied != expected.Content) return new(PublishState.Conflict, "storage_copy_integrity");
                    Flush(destinationFile, operation);
                }
            }
            await using var completed = OpenFile(destination, FileMode.Open, FileAccess.Read, operation);
            var verified = await VerifyAsync(completed, expected.Content, operation);
            return verified.State == InspectionState.Matching ? new(PublishState.Published) : FromInspection(verified);
        }
        catch (StorageUnavailableException exception) { return new(PublishState.Unavailable, Reason(exception), exception); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new(PublishState.Unknown, Reason(exception), exception); }
    }

    public async ValueTask<OpenOutcome> OpenVerifiedAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken)
    {
        if (!Valid(expected, budget)) return new OpenOutcome.Rejected(new(InspectionState.Unavailable, "storage_input_invalid"));
        using var operation = new StorageOperation(budget, cancellationToken);
        FileStream? file = null;
        try
        {
            CheckNamespace(operation);
            var path = OriginalPath(expected.Attempt);
            if (!root.ValidateLeaf(path)) return new OpenOutcome.Rejected(new(InspectionState.Absent));
            file = OpenFile(path, FileMode.Open, FileAccess.Read, operation);
            var inspection = await VerifyAsync(file, expected.Content, operation);
            if (inspection.State != InspectionState.Matching) return new OpenOutcome.Rejected(inspection);
            operation.Check();
            var owned = new VerifiedOriginalHandle(file, expected);
            file = null;
            return new OpenOutcome.Opened(owned);
        }
        catch (FileNotFoundException) { return new OpenOutcome.Rejected(new(InspectionState.Absent)); }
        catch (StorageUnavailableException exception) { return new OpenOutcome.Rejected(new(InspectionState.Unavailable, Reason(exception), exception)); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new OpenOutcome.Rejected(new(InspectionState.Unknown, Reason(exception), exception)); }
        finally { if (file is not null) await file.DisposeAsync(); }
    }

    public async ValueTask<NativeOpenOutcome> OpenVerifiedForNativeValidationAsync(ExpectedPublication expected, StorageIoBudget budget, CancellationToken cancellationToken)
    {
        if (!Valid(expected, budget)) return new NativeOpenOutcome.Rejected(new(InspectionState.Unavailable, "storage_input_invalid"));
        using var operation = new StorageOperation(budget, cancellationToken);
        FileStream? file = null;
        try
        {
            CheckNamespace(operation);
            var path = OriginalPath(expected.Attempt);
            if (!root.ValidateLeaf(path)) return new NativeOpenOutcome.Rejected(new(InspectionState.Absent));
            // Only this acquisition permits another reader. Write/delete sharing stays denied throughout verification and native use.
            file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = operation.Budget.BufferBytes,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan, PreallocationSize = 0
            });
            root.ValidateFile(file); operation.Check();
            var inspection = await VerifyAsync(file, expected.Content, operation);
            if (inspection.State != InspectionState.Matching) return new NativeOpenOutcome.Rejected(inspection);
            CheckNamespace(operation); root.ValidateLeaf(path); root.ValidateFile(file); operation.Check();
            var owned = new VerifiedNativeOriginalHandle(file, expected);
            operation.Check();
            file = null;
            return new NativeOpenOutcome.Opened(owned);
        }
        catch (FileNotFoundException) { return new NativeOpenOutcome.Rejected(new(InspectionState.Absent)); }
        catch (StorageUnavailableException exception) { return new NativeOpenOutcome.Rejected(new(InspectionState.Unavailable, Reason(exception), exception)); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new NativeOpenOutcome.Rejected(new(InspectionState.Unknown, Reason(exception), exception)); }
        finally { if (file is not null) await file.DisposeAsync(); }
    }

    private bool Valid(ExpectedPublication? expected, StorageIoBudget? budget) =>
        expected is not null && budget is not null && expected.Attempt.StoreId == storeId && expected.Content.Length <= budget.MaximumBytes;
    private void CheckNamespace(StorageOperation operation) { operation.Check(); root.Validate(); operation.Check(); }
    private string StagePath(StorageAttempt attempt, string suffix) => Path.Combine(root.Path, attempt.StageToken + suffix);
    private string OriginalPath(StorageAttempt attempt) => Path.Combine(root.Path, attempt.Generation + ".original");

    private FileStream OpenFile(string path, FileMode mode, FileAccess access, StorageOperation operation)
    {
        CheckNamespace(operation);
        root.ValidateLeaf(path);
        var file = new FileStream(path, new FileStreamOptions
        {
            Mode = mode, Access = access, Share = FileShare.None, BufferSize = operation.Budget.BufferBytes,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, PreallocationSize = 0
        });
        try { root.ValidateFile(file); operation.Check(); return file; }
        catch { file.Dispose(); throw; } // Closes only; never deletes an ambiguous candidate.
    }
    private static void Flush(FileStream file, StorageOperation operation)
    {
        operation.Check();
        file.Flush(flushToDisk: true); // Synchronous OS call: no hard wall-time cancellation guarantee.
        operation.Check();
    }
    private async ValueTask<PublishOutcome> RecoverPendingAsync(string path, StorageContent content, StorageOperation operation)
    {
        // Caller must supply a still-unfinalized reservation. No content mutation is exposed.
        await using var file = OpenFile(path, FileMode.Open, FileAccess.ReadWrite, operation);
        var inspection = await VerifyAsync(file, content, operation);
        if (inspection.State != InspectionState.Matching) return FromInspection(inspection);
        Flush(file, operation);
        return new(PublishState.ExistingIdentical);
    }
    private static PublishOutcome FromInspection(StorageInspection inspection) => new(inspection.State switch
    {
        InspectionState.Incomplete => PublishState.Incomplete,
        InspectionState.Conflict => PublishState.Conflict,
        InspectionState.Unavailable => PublishState.Unavailable,
        _ => PublishState.Unknown
    }, inspection.Reason, inspection.Failure);
    private static async ValueTask<StorageInspection> VerifyAsync(FileStream file, StorageContent expected, StorageOperation operation)
    {
        operation.Check();
        if (file.Length < expected.Length) return new(InspectionState.Incomplete);
        if (file.Length > expected.Length) return new(InspectionState.Conflict);
        file.Position = 0;
        StorageContent actual;
        try { actual = await CopyAndHashAsync(file, null, expected.Length, operation); }
        catch (StorageLimitException) { return new(InspectionState.Conflict); }
        if (actual != expected) return new(InspectionState.Conflict);
        file.Position = 0;
        operation.Check();
        return new(InspectionState.Matching);
    }
    private static async ValueTask<StorageContent> CopyAndHashAsync(Stream source, Stream? destination, long maximumBytes, StorageOperation operation, bool externalSource = false)
    {
        var buffer = new byte[operation.Budget.BufferBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            operation.Check();
            var remaining = maximumBytes - length;
            var requested = remaining == 0 ? 1 : (int)Math.Min(buffer.Length, remaining);
            int read;
            try { read = await source.ReadAsync(buffer.AsMemory(0, requested), operation.Token); }
            catch (Exception exception) when (externalSource && !IsStorageFailure(exception) &&
                exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                throw new StorageSourceException(exception);
            }
            operation.Check();
            if (read < 0 || read > requested) throw new IOException("Source violated its stream contract.");
            if (read == 0) break;
            if (read > remaining) throw new StorageLimitException();
            if (destination is not null) await destination.WriteAsync(buffer.AsMemory(0, read), operation.Token);
            hash.AppendData(buffer, 0, read);
            length += read; // Bounded by maximumBytes; no max+1 arithmetic.
        }
        if (length == 0) throw new StorageLimitException();
        operation.Check();
        return new StorageContent(length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private static bool IsCollision(IOException exception) => (exception.HResult & 0xffff) is 80 or 183;
    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException or StorageLimitException or StorageUnavailableException or StorageSourceException;
    private static string Reason(Exception exception) => exception switch
    {
        StorageUnavailableException => exception.Message,
        StorageLimitException => "storage_content_limit",
        StorageSourceException => "storage_source_interrupted",
        OperationCanceledException => "storage_cancelled_or_deadline",
        _ => "storage_io_unknown"
    };
}

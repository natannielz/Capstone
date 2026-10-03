using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Worker;

internal sealed class SupplierDocumentOcrWorker(SupplierDocumentOcrRuntime runtime, ILogger<SupplierDocumentOcrWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested) {
            string status;
            try { status = await runtime.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PersistenceConflictException) { status = "claim_or_policy_changed"; }
            catch (UncertainCommitException) { status = "commit_unknown_recover_same_job"; }
            catch { status = "processing_error"; } // Never log original content, private paths or exception payloads.
            if (status != previous) { logger.LogInformation("Supplier document OCR worker status: {Status}", status); previous = status; }
            await Task.Delay(status == "completed" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

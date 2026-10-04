using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Worker;

internal sealed class PdfDocumentOcrWorker(IEnumerable<PdfDocumentOcrRuntime> configured, ILogger<PdfDocumentOcrWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var runtimes = configured.ToArray(); var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested) {
            var completed = false;
            foreach (var runtime in runtimes) {
                string status;
                try { status = await runtime.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (PersistenceConflictException) { status = "claim_or_policy_changed"; }
                catch (UncertainCommitException) { status = "commit_unknown_recover_same_job"; }
                catch { status = "processing_error"; } // Log no original contents, private paths, pins or provider configuration.
                if (!previous.TryGetValue(runtime.SourceKind, out var old) || status != old) {
                    logger.LogInformation("Whole-PDF {SourceKind} worker status: {Status}", runtime.SourceKind, status); previous[runtime.SourceKind] = status;
                }
                completed |= status == "completed";
            }
            await Task.Delay(completed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

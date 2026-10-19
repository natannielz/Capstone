using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Worker;

/// <summary>Approved D02 retention: purges order drafts 30 days after they were discarded. A full batch continues
/// shortly; otherwise the next pass runs in six hours. Logs bounded status only, never draft content.</summary>
internal sealed class OrderDraftPurgeWorker(OrderDraftPurge purge, ILogger<OrderDraftPurgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested) {
            string status; var full = false;
            try {
                var result = await purge.RunOnceAsync(stoppingToken); full = result.Purged + result.Refused >= OrderDraftPurge.BatchLimit;
                status = result.Refused > 0 ? "integrity_refused" : result.Purged > 0 ? "purged" : "idle";
                if (result.Purged > 0 || result.Refused > 0)
                    logger.LogInformation("Order draft purge: {Purged} purged, {Refused} refused", result.Purged, result.Refused);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (UncertainCommitException) { status = "commit_unknown_retry"; }
            catch { status = "processing_error"; }
            if (status != previous) { logger.LogInformation("Order draft purge status: {Status}", status); previous = status; }
            await Task.Delay(full ? TimeSpan.FromSeconds(5) : status == "processing_error" ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(6), stoppingToken);
        }
    }
}

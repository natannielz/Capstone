using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Worker;

internal sealed class PaymentReviewNotificationWorker(PaymentReviewNotifications runtime, ILogger<PaymentReviewNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested) {
            string status;
            try { status = await runtime.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (UncertainCommitException) { status = "commit_unknown_recover_same_job"; }
            catch { status = "processing_error"; } // Log only bounded status, never private source/reason or exception payloads.
            if (status != previous) { logger.LogInformation("Payment in-app notice status: {Status}", status); previous = status; }
            await Task.Delay(status == "completed" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

namespace UnitToko.Worker;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("No durable outbox processor has been implemented.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}

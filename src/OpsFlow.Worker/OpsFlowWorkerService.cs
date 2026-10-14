using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Infrastructure.Options;

namespace OpsFlow.Worker;

public sealed class OpsFlowWorkerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<OpsFlowWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OpsFlow worker started. Poll interval {Seconds}s.", options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var escalations = await scope.ServiceProvider.GetRequiredService<ISlaEscalationScanner>().ScanAsync(stoppingToken);
                var processed = await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync(stoppingToken);
                var cleaned = await scope.ServiceProvider.GetRequiredService<IIdempotencyCleaner>().CleanupAsync(stoppingToken);
                if (escalations + processed + cleaned > 0)
                {
                    logger.LogInformation("Worker cycle recorded {Escalations} escalations, processed {Jobs} jobs, and cleaned {Records} idempotency records.", escalations, processed, cleaned);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Worker cycle failed and will retry on the next interval.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("OpsFlow worker stopping.");
    }
}

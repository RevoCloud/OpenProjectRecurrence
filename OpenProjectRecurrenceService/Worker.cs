using Microsoft.Extensions.Options;

namespace OpenProjectRecurrenceService;

public sealed class Worker(
    RecurrenceProcessor recurrenceProcessor,
    IOptions<OpenProjectOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromMinutes(Math.Max(1, options.Value.PollingIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await recurrenceProcessor.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("OpenProject recurrence scheduler is shutting down gracefully.");
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unhandled error occurred while running the OpenProject recurrence scheduler.");
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(pollingInterval, stoppingToken);
        }
    }
}

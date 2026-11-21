namespace IPBlueprint.Store.Shop;

/// <summary>Runs <see cref="DataRetention"/> at startup and then once a day.</summary>
public sealed class DataRetentionJob(IServiceScopeFactory scopes, ILogger<DataRetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DataRetention>().CleanUpAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not delete old personal data; will try again tomorrow");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Runs what <see cref="BackgroundWorkQueue"/> holds, one item at a time. On a normal shutdown (a deploy) it finishes
/// the queue before the app stops. At startup it sends any back-in-stock emails a previous run didn't get to.
/// </summary>
public sealed class BackgroundWorker(BackgroundWorkQueue queue, IServiceScopeFactory scopes, ILogger<BackgroundWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleCheck = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Enqueue((services, ct) => services.GetRequiredService<StockAlerts>().NotifyAllAvailableAsync(ct));

        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await work(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Background work failed");
                }
                finally
                {
                    queue.MarkDone();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Forced to stop before the queue was empty: the shutdown timeout ran out.
        }
    }

    /// <summary>Waits for the queue to empty (or the host's shutdown timeout) before stopping, so a deploy doesn't drop
    /// emails that were just queued.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        while (!queue.IsIdle && !cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(IdleCheck, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        if (!queue.IsIdle) logger.LogWarning("Stopping with background work still queued; it will not run");
        await base.StopAsync(cancellationToken);
    }
}

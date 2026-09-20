using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;

namespace WDWAPP.Services;

public sealed class AdvertisementCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AdvertisementCleanup> logger) : BackgroundService
{
    public const int BatchSize = 200;
    // Cascades remove image blobs and contact rows, without loading them into memory.
    public static Task<int> DeleteExpiredBatchAsync(ApplicationDbContext database, DateTime now, CancellationToken cancellationToken = default)
        => database.Advertisements.Where(a => a.ExpiresAt <= now).OrderBy(a => a.ExpiresAt).ThenBy(a => a.Id)
            .Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    while (await DeleteExpiredBatchAsync(database, clock.GetUtcNow().UtcDateTime, stoppingToken) == BatchSize)
                        stoppingToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Could not clean up expired advertisements; retrying on the next interval."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

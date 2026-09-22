using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;

namespace WDWAPP.Services;

public sealed class MatchRequestCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MatchRequestCleanup> logger) : BackgroundService
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    public const int BatchSize = 200;

    // Deleting a request also deletes its calendar entry through the foreign key cascade.
    public static Task<int> DeleteExpiredBatchAsync(ApplicationDbContext database, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Stockholm).DateTime);
        return database.MatchRequests.Where(r => r.CalendarEvent.Date < today)
            .OrderBy(r => r.Id).Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    while (await DeleteExpiredBatchAsync(database, clock.GetUtcNow(), stoppingToken) == BatchSize)
                        stoppingToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Could not clean up expired match requests; retrying on the next interval."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

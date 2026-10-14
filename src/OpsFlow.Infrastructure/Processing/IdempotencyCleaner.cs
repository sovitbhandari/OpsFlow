using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Processing;

public sealed class IdempotencyCleaner(IDbContextFactory<OpsFlowDbContext> dbFactory, TimeProvider timeProvider, ILogger<IdempotencyCleaner> logger) : IIdempotencyCleaner
{
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.IdempotencyRecords
            .Where(r => r.ExpiresAtUtc < now && (r.Status == IdempotencyStatus.Completed || r.LeaseUntilUtc < now))
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} expired idempotency records.", deleted);
        }

        return deleted;
    }
}

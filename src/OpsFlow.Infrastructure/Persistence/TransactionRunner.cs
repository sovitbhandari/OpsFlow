using Microsoft.EntityFrameworkCore;

namespace OpsFlow.Infrastructure.Persistence;

public sealed class TransactionRunner(IDbContextFactory<OpsFlowDbContext> dbFactory)
{
    public async Task<T> ExecuteAsync<T>(Func<OpsFlowDbContext, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await action(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class IdempotencyCoordinator(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    TransactionRunner transactions,
    TimeProvider timeProvider,
    IOptions<IdempotencyOptions> options)
{
    public async Task<IdempotentHttpResult> ExecuteAsync(
        Guid organizationId,
        Guid userId,
        string operation,
        string key,
        string requestHash,
        Func<CancellationToken, Task> reauthorize,
        Func<OpsFlowDbContext, CancellationToken, Task<IdempotentHttpResult>> work,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length is < 8 or > 128)
        {
            throw new AppValidationException(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Provide an idempotency key between 8 and 128 characters."]
            });
        }

        key = key.Trim();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var settings = options.Value;

        var existing = await FindAsync(organizationId, userId, operation, key, cancellationToken);
        if (existing is not null)
        {
            EnsureSamePayload(existing, requestHash);
            if (existing.Status == IdempotencyStatus.Completed && existing.ExpiresAtUtc > now)
            {
                await reauthorize(cancellationToken);
                return new IdempotentHttpResult(existing.ResponseStatusCode ?? 200, existing.ResponseBody ?? "{}", true);
            }

            if (existing.LeaseUntilUtc > now)
            {
                throw new ConflictException("A request with this idempotency key is already in progress.");
            }

            if (!await TryTakeLeaseAsync(existing.Id, now, now.AddSeconds(settings.InProgressLeaseSeconds), cancellationToken))
            {
                throw new ConflictException("A request with this idempotency key is already in progress.");
            }

            return await RunWorkAsync(existing.Id, reauthorize, work, cancellationToken);
        }

        var createdId = await TryInsertAsync(organizationId, userId, operation, key, requestHash, now, settings, cancellationToken);
        if (createdId is null)
        {
            existing = await FindAsync(organizationId, userId, operation, key, cancellationToken)
                ?? throw new ConflictException("A request with this idempotency key is already in progress.");
            EnsureSamePayload(existing, requestHash);
            if (existing.Status == IdempotencyStatus.Completed)
            {
                await reauthorize(cancellationToken);
                return new IdempotentHttpResult(existing.ResponseStatusCode ?? 200, existing.ResponseBody ?? "{}", true);
            }

            if (existing.LeaseUntilUtc > timeProvider.GetUtcNow().UtcDateTime)
            {
                throw new ConflictException("A request with this idempotency key is already in progress.");
            }

            if (!await TryTakeLeaseAsync(existing.Id, timeProvider.GetUtcNow().UtcDateTime, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(settings.InProgressLeaseSeconds), cancellationToken))
            {
                throw new ConflictException("A request with this idempotency key is already in progress.");
            }

            return await RunWorkAsync(existing.Id, reauthorize, work, cancellationToken);
        }

        return await RunWorkAsync(createdId.Value, reauthorize, work, cancellationToken);
    }

    private async Task<IdempotentHttpResult> RunWorkAsync(
        Guid recordId,
        Func<CancellationToken, Task> reauthorize,
        Func<OpsFlowDbContext, CancellationToken, Task<IdempotentHttpResult>> work,
        CancellationToken cancellationToken)
    {
        await reauthorize(cancellationToken);
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var record = await db.IdempotencyRecords.FirstAsync(r => r.Id == recordId, ct);
            if (record.Status == IdempotencyStatus.Completed)
            {
                await reauthorize(ct);
                return new IdempotentHttpResult(record.ResponseStatusCode ?? 200, record.ResponseBody ?? "{}", true);
            }

            var result = await work(db, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            record.Status = IdempotencyStatus.Completed;
            record.ResponseStatusCode = result.StatusCode;
            record.ResponseBody = result.Body.Length > 16000 ? result.Body[..16000] : result.Body;
            record.ExpiresAtUtc = now.AddHours(options.Value.CompletedRetentionHours);
            await db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }

    private async Task<Guid?> TryInsertAsync(
        Guid organizationId,
        Guid userId,
        string operation,
        string key,
        string requestHash,
        DateTime now,
        IdempotencyOptions settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactions.ExecuteAsync(async (db, ct) =>
            {
                var record = new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    UserId = userId,
                    Operation = operation,
                    Key = key,
                    RequestHash = requestHash,
                    Status = IdempotencyStatus.InProgress,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = now.AddHours(settings.CompletedRetentionHours),
                    LeaseUntilUtc = now.AddSeconds(settings.InProgressLeaseSeconds)
                };
                db.IdempotencyRecords.Add(record);
                await db.SaveChangesAsync(ct);
                return (Guid?)record.Id;
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolation(exception))
        {
            return null;
        }
    }

    private async Task<bool> TryTakeLeaseAsync(Guid id, DateTime now, DateTime leaseUntil, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.IdempotencyRecords
            .Where(r => r.Id == id && r.Status == IdempotencyStatus.InProgress && r.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.LeaseUntilUtc, leaseUntil), cancellationToken);
        return updated == 1;
    }

    private async Task<IdempotencyRecord?> FindAsync(
        Guid organizationId,
        Guid userId,
        string operation,
        string key,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.UserId == userId && r.Operation == operation && r.Key == key, cancellationToken);
    }

    private static void EnsureSamePayload(IdempotencyRecord existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new ConflictException("This idempotency key was already used with a different request.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class NotificationAdminService(IDbContextFactory<OpsFlowDbContext> dbFactory, TimeProvider timeProvider) : INotificationAdminService
{
    public async Task<PagedResult<NotificationJobDto>> ListAsync(TenantActor actor, int page, int pageSize, CancellationToken cancellationToken)
    {
        var paging = RequestValidation.NormalizePaging(page, pageSize);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanInspectNotifications(membership.Role))
        {
            throw new ForbiddenException("Only an organization admin can inspect notification jobs.");
        }

        var query = db.OutboxMessages.AsNoTracking().Where(m => m.OrganizationId == actor.OrganizationId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(m => m.CreatedAtUtc).ThenBy(m => m.Id)
            .Skip((paging.Page - 1) * paging.PageSize)
            .Take(paging.PageSize)
            .Select(m => new NotificationJobDto(m.Id, m.Type, m.Status, m.AttemptCount, m.NextAttemptAtUtc, m.LeaseUntilUtc, m.LastError, m.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new PagedResult<NotificationJobDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<NotificationJobDto> RetryAsync(TenantActor actor, Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanInspectNotifications(membership.Role))
        {
            throw new ForbiddenException("Only an organization admin can retry notification jobs.");
        }

        var job = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == jobId && m.OrganizationId == actor.OrganizationId, cancellationToken);
        if (job is null)
        {
            throw new NotFoundException("Notification job was not found.");
        }

        if (job.Status is not (OutboxStatus.Failed or OutboxStatus.Succeeded))
        {
            throw new ConflictException("Only failed or completed jobs can be retried.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        job.Status = OutboxStatus.Pending;
        job.AttemptCount = 0;
        job.NextAttemptAtUtc = now;
        job.LeaseUntilUtc = null;
        job.LastError = null;
        job.UpdatedAtUtc = now;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actor.UserId,
            Action = "NotificationRetried",
            EntityType = "OutboxMessage",
            EntityId = job.Id.ToString(),
            Summary = $"Queued a manual retry for {job.Type}.",
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new NotificationJobDto(job.Id, job.Type, job.Status, job.AttemptCount, job.NextAttemptAtUtc, job.LeaseUntilUtc, job.LastError, job.CreatedAtUtc);
    }
}

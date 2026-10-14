using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class AuditQueryService(IDbContextFactory<OpsFlowDbContext> dbFactory) : IAuditQueryService
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(TenantActor actor, int page, int pageSize, CancellationToken cancellationToken)
    {
        var paging = RequestValidation.NormalizePaging(page, pageSize);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanViewAudit(membership.Role))
        {
            throw new ForbiddenException("Only an organization admin can view the audit history.");
        }

        var query = db.AuditLogs.AsNoTracking().Where(a => a.OrganizationId == actor.OrganizationId);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id)
            .Skip((paging.Page - 1) * paging.PageSize)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, rows.Where(r => r.ActorUserId.HasValue).Select(r => r.ActorUserId!.Value), cancellationToken);
        var items = rows.Select(row => new AuditLogDto(
            row.Id,
            row.ActorUserId,
            row.ActorUserId is Guid id && names.TryGetValue(id, out var name) ? name : null,
            row.Action,
            row.EntityType,
            row.EntityId,
            row.Summary,
            row.CreatedAtUtc)).ToList();
        return new PagedResult<AuditLogDto>(items, paging.Page, paging.PageSize, total);
    }
}

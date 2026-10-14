using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class OrganizationService(IDbContextFactory<OpsFlowDbContext> dbFactory, TransactionRunner transactions, TimeProvider timeProvider) : IOrganizationService
{
    public async Task<IReadOnlyList<OrganizationDto>> ListMineAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Organizations.AsNoTracking(), m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Name, o.Slug, m.Role })
            .OrderBy(row => row.Name)
            .Select(row => new OrganizationDto(row.Id, row.Name, row.Slug, row.Role))
            .ToListAsync(cancellationToken);
    }

    public async Task<OrganizationDto> GetAsync(TenantActor actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == actor.OrganizationId, cancellationToken);
        return new OrganizationDto(organization.Id, organization.Name, organization.Slug, membership.Role);
    }

    public async Task<OrganizationDto> UpdateAsync(TenantActor actor, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["name"] = ["Name is required and must be at most 200 characters."] });
        }

        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanManageOrganization(membership.Role))
            {
                throw new ForbiddenException("Only an organization admin can update organization settings.");
            }

            var organization = await db.Organizations.FirstAsync(o => o.Id == actor.OrganizationId, ct);
            organization.Name = name.Trim();
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrganizationId = actor.OrganizationId,
                ActorUserId = actor.UserId,
                Action = "OrganizationUpdated",
                EntityType = "Organization",
                EntityId = organization.Id.ToString(),
                Summary = "Organization name was updated.",
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(ct);
            return new OrganizationDto(organization.Id, organization.Name, organization.Slug, membership.Role);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SlaPolicyDto>> GetSlaPoliciesAsync(TenantActor actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        return await db.SlaPolicies.AsNoTracking()
            .Where(p => p.OrganizationId == actor.OrganizationId)
            .OrderBy(p => p.Priority)
            .Select(p => new SlaPolicyDto(p.Priority, p.DurationSeconds, p.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SlaPolicyDto>> UpdateSlaPoliciesAsync(TenantActor actor, IReadOnlyList<UpdateSlaPolicyRequest> policies, CancellationToken cancellationToken)
    {
        RequestValidation.EnsureSla(policies);
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanManageSla(membership.Role))
            {
                throw new ForbiddenException("Only an organization admin can update SLA policies.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var existing = await db.SlaPolicies.Where(p => p.OrganizationId == actor.OrganizationId).ToListAsync(ct);
            foreach (var request in policies)
            {
                var policy = existing.FirstOrDefault(p => p.Priority == request.Priority);
                if (policy is null)
                {
                    policy = new SlaPolicy { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, Priority = request.Priority };
                    db.SlaPolicies.Add(policy);
                }

                policy.DurationSeconds = request.DurationSeconds;
                policy.UpdatedAtUtc = now;
            }

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrganizationId = actor.OrganizationId,
                ActorUserId = actor.UserId,
                Action = "SlaPoliciesUpdated",
                EntityType = "SlaPolicy",
                EntityId = actor.OrganizationId.ToString(),
                Summary = "Elapsed-time SLA durations were updated. On-hold still pauses the SLA clock.",
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            return policies.Select(p => new SlaPolicyDto(p.Priority, p.DurationSeconds, now)).OrderBy(p => p.Priority).ToList();
        }, cancellationToken);
    }
}

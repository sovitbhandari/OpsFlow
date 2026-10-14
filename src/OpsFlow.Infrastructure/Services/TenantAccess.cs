using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

internal static class TenantAccess
{
    public static async Task<OrganizationMembership> RequireMemberAsync(OpsFlowDbContext db, TenantActor actor, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == actor.UserId, cancellationToken);
        if (membership is null)
        {
            throw new NotFoundException("Organization was not found.");
        }

        return membership;
    }

    public static async Task<Dictionary<Guid, string>> NamesAsync(OpsFlowDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }
}

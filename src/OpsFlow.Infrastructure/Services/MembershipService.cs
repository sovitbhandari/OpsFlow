using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class MembershipService(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    TransactionRunner transactions,
    UserManager<ApplicationUser> users,
    TimeProvider timeProvider) : IMembershipService
{
    public async Task<IReadOnlyList<MembershipDto>> ListAsync(TenantActor actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanManageMembers(membership.Role) && !PermissionMatrix.CanAssign(membership.Role))
        {
            throw new ForbiddenException("You cannot view organization members.");
        }

        return await Query(db, actor.OrganizationId).OrderBy(m => m.DisplayName).ToListAsync(cancellationToken);
    }

    public async Task<MembershipDto> AddAsync(TenantActor actor, CreateMemberRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var existingUser = await users.FindByEmailAsync(email);
        RequestValidation.EnsureMember(request, passwordRequired: existingUser is null);

        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var actorMembership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanManageMembers(actorMembership.Role))
            {
                throw new ForbiddenException("Only an organization admin can add members.");
            }

            ApplicationUser user;
            if (existingUser is null)
            {
                user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    UserName = email,
                    Email = email,
                    DisplayName = request.DisplayName.Trim(),
                    EmailConfirmed = true,
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                var created = await users.CreateAsync(user, request.Password!);
                if (!created.Succeeded)
                {
                    throw new AppValidationException(new Dictionary<string, string[]>
                    {
                        ["password"] = created.Errors.Select(error => error.Description).ToArray()
                    });
                }
            }
            else
            {
                user = existingUser;
            }

            var duplicate = await db.Memberships.AnyAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == user.Id, ct);
            if (duplicate)
            {
                throw new ConflictException("That user is already a member of this organization.");
            }

            var membership = new OrganizationMembership
            {
                Id = Guid.NewGuid(),
                OrganizationId = actor.OrganizationId,
                UserId = user.Id,
                Role = request.Role,
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            db.Memberships.Add(membership);
            db.AuditLogs.Add(Audit(actor, "MemberAdded", user.Id.ToString(), $"Added {user.Email} as {request.Role}."));
            await db.SaveChangesAsync(ct);
            return new MembershipDto(user.Id, user.Email ?? email, user.DisplayName, membership.Role, membership.CreatedAtUtc);
        }, cancellationToken);
    }

    public async Task<MembershipDto> UpdateRoleAsync(TenantActor actor, Guid userId, OrganizationRole role, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["role"] = ["Role is invalid."] });
        }

        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var actorMembership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanManageMembers(actorMembership.Role))
            {
                throw new ForbiddenException("Only an organization admin can change roles.");
            }

            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == userId, ct);
            if (membership is null)
            {
                throw new NotFoundException("Member was not found.");
            }

            await EnsureAdminRemainsAsync(db, actor.OrganizationId, membership, role, removing: false, ct);
            membership.Role = role;
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            db.AuditLogs.Add(Audit(actor, "MemberRoleChanged", userId.ToString(), $"Changed {user.Email} to {role}."));
            await db.SaveChangesAsync(ct);
            return new MembershipDto(user.Id, user.Email ?? "", user.DisplayName, membership.Role, membership.CreatedAtUtc);
        }, cancellationToken);
    }

    public async Task RemoveAsync(TenantActor actor, Guid userId, CancellationToken cancellationToken)
    {
        await transactions.ExecuteAsync(async (db, ct) =>
        {
            var actorMembership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanManageMembers(actorMembership.Role))
            {
                throw new ForbiddenException("Only an organization admin can remove members.");
            }

            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == actor.OrganizationId && m.UserId == userId, ct);
            if (membership is null)
            {
                throw new NotFoundException("Member was not found.");
            }

            var referenced = await db.WorkOrders.AnyAsync(
                w => w.OrganizationId == actor.OrganizationId && (w.RequesterUserId == userId || w.AssignedTechnicianUserId == userId), ct);
            if (referenced)
            {
                throw new ConflictException("This member is a requester or assignee on a work order and cannot be removed.");
            }

            await EnsureAdminRemainsAsync(db, actor.OrganizationId, membership, membership.Role, removing: true, ct);
            db.Memberships.Remove(membership);
            db.AuditLogs.Add(Audit(actor, "MemberRemoved", userId.ToString(), "Removed a member."));
            await db.SaveChangesAsync(ct);
            return 0;
        }, cancellationToken);
    }

    private static async Task EnsureAdminRemainsAsync(
        OpsFlowDbContext db,
        Guid organizationId,
        OrganizationMembership membership,
        OrganizationRole nextRole,
        bool removing,
        CancellationToken cancellationToken)
    {
        if (membership.Role != OrganizationRole.Admin)
        {
            return;
        }

        if (!removing && nextRole == OrganizationRole.Admin)
        {
            return;
        }

        var otherAdmins = await db.Memberships.CountAsync(
            m => m.OrganizationId == organizationId && m.Role == OrganizationRole.Admin && m.UserId != membership.UserId, cancellationToken);
        if (otherAdmins == 0)
        {
            throw new ConflictException("The organization must keep at least one admin.");
        }
    }

    private static IQueryable<MembershipDto> Query(OpsFlowDbContext db, Guid organizationId)
        => db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new MembershipDto(u.Id, u.Email ?? "", u.DisplayName, m.Role, m.CreatedAtUtc));

    private AuditLog Audit(TenantActor actor, string action, string entityId, string summary)
        => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actor.UserId,
            Action = action,
            EntityType = "Membership",
            EntityId = entityId,
            Summary = summary,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
}

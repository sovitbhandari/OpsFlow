using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class WorkOrderService(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    TransactionRunner transactions,
    IdempotencyCoordinator idempotency,
    TimeProvider timeProvider) : IWorkOrderService
{
    private const string CreateOperation = "work-orders.create";
    private const string ClaimOperation = "work-orders.claim";

    public async Task<PagedResult<WorkOrderSummaryDto>> ListAsync(TenantActor actor, WorkOrderListQuery query, CancellationToken cancellationToken)
    {
        var (page, pageSize) = RequestValidation.NormalizePaging(query.Page, query.PageSize);
        if (query.Search is { Length: > 200 })
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["search"] = ["Search must be at most 200 characters."] });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        var workOrders = db.WorkOrders.AsNoTracking().Where(w => w.OrganizationId == actor.OrganizationId);
        workOrders = ApplyVisibility(workOrders, membership.Role, actor.UserId);
        workOrders = ApplyFilters(workOrders, query);
        workOrders = ApplySort(workOrders, query.Sort, query.Direction);

        var total = await workOrders.CountAsync(cancellationToken);
        var rows = await workOrders.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, rows.SelectMany(RowUserIds), cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = rows.Select(row => ToSummary(row, names, now)).ToList();
        return new PagedResult<WorkOrderSummaryDto>(items, page, pageSize, total);
    }

    public async Task<WorkOrderDetailDto> GetAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        var workOrder = await LoadVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        var names = await TenantAccess.NamesAsync(db, UserIds(workOrder), cancellationToken);
        return ToDetail(workOrder, names, timeProvider.GetUtcNow().UtcDateTime);
    }

    public Task<IdempotentHttpResult> CreateAsync(TenantActor actor, CreateWorkOrderRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        RequestValidation.EnsureCreate(request);
        return idempotency.ExecuteAsync(
            actor.OrganizationId,
            actor.UserId,
            CreateOperation,
            idempotencyKey,
            Fingerprint.ForCreate(request),
            ct => ReauthorizeAsync(actor, PermissionMatrix.CanCreateWorkOrder, ct),
            (db, ct) => CreateCoreAsync(db, actor, request, ct),
            cancellationToken);
    }

    public Task<IdempotentHttpResult> ClaimAsync(TenantActor actor, Guid workOrderId, string idempotencyKey, CancellationToken cancellationToken)
        => idempotency.ExecuteAsync(
            actor.OrganizationId,
            actor.UserId,
            ClaimOperation,
            idempotencyKey,
            Fingerprint.ForClaim(workOrderId),
            ct => ReauthorizeAsync(actor, PermissionMatrix.CanClaim, ct),
            (db, ct) => ClaimCoreAsync(db, actor, workOrderId, ct),
            cancellationToken);

    public async Task<WorkOrderDetailDto> UpdateAsync(TenantActor actor, Guid workOrderId, string rowVersion, UpdateWorkOrderRequest request, CancellationToken cancellationToken)
    {
        RequestValidation.EnsureUpdate(request);
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            var workOrder = await LoadTrackedVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            if (!PermissionMatrix.CanEditDetails(membership.Role, workOrder.RequesterUserId == actor.UserId, workOrder.Status))
            {
                throw new ForbiddenException("You cannot edit this work order.");
            }

            SetClientRowVersion(db, workOrder, rowVersion);
            var priorityChanged = workOrder.Priority != request.Priority;
            workOrder.Title = request.Title.Trim();
            workOrder.Description = request.Description.Trim();
            workOrder.Category = request.Category.Trim();
            workOrder.Priority = request.Priority;
            if (priorityChanged)
            {
                var duration = await RequireSlaDurationAsync(db, actor.OrganizationId, request.Priority, ct);
                SlaClock.ApplyPriority(workOrder, duration);
            }

            workOrder.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            AddActivity(db, workOrder, actor.UserId, "Updated", "Details were updated.");
            AddAudit(db, actor, "WorkOrderUpdated", "WorkOrder", workOrder.Id.ToString(), $"Updated {WorkOrderFormatting.DisplayNumber(workOrder.Number)}.");
            await SaveHandlingConcurrencyAsync(db, ct);
            return await MapAsync(db, workOrder, ct);
        }, cancellationToken);
    }

    public async Task<WorkOrderDetailDto> AssignAsync(TenantActor actor, Guid workOrderId, string rowVersion, Guid technicianUserId, CancellationToken cancellationToken)
    {
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanAssign(membership.Role))
            {
                throw new ForbiddenException("Only an admin or dispatcher can assign work orders.");
            }

            var workOrder = await LoadTrackedVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            if (workOrder.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed or WorkOrderStatus.Cancelled or WorkOrderStatus.OnHold)
            {
                throw new InvalidTransitionException("This work order cannot be assigned in its current status.");
            }

            var technician = await db.Memberships.AsNoTracking().FirstOrDefaultAsync(
                m => m.OrganizationId == actor.OrganizationId && m.UserId == technicianUserId && m.Role == OrganizationRole.Technician, ct);
            if (technician is null)
            {
                throw new NotFoundException("Technician was not found in this organization.");
            }

            SetClientRowVersion(db, workOrder, rowVersion);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            workOrder.AssignedTechnicianUserId = technicianUserId;
            if (workOrder.Status == WorkOrderStatus.Open)
            {
                workOrder.Status = WorkOrderStatus.Assigned;
            }

            workOrder.UpdatedAtUtc = now;
            AddActivity(db, workOrder, actor.UserId, "Assigned", "Work order was assigned.");
            AddAudit(db, actor, "WorkOrderAssigned", "WorkOrder", workOrder.Id.ToString(), $"Assigned {WorkOrderFormatting.DisplayNumber(workOrder.Number)}.");
            await EnqueueAsync(db, actor.OrganizationId, "WorkOrderAssigned", workOrder, new[] { technicianUserId }, now, ct);
            await SaveHandlingConcurrencyAsync(db, ct);
            return await MapAsync(db, workOrder, ct);
        }, cancellationToken);
    }

    public async Task<WorkOrderDetailDto> UnassignAsync(TenantActor actor, Guid workOrderId, string rowVersion, CancellationToken cancellationToken)
    {
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            if (!PermissionMatrix.CanAssign(membership.Role))
            {
                throw new ForbiddenException("Only an admin or dispatcher can unassign work orders.");
            }

            var workOrder = await LoadTrackedVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            if (workOrder.Status != WorkOrderStatus.Assigned || workOrder.AssignedTechnicianUserId is null)
            {
                throw new InvalidTransitionException("Only an assigned work order that has not started can be unassigned.");
            }

            SetClientRowVersion(db, workOrder, rowVersion);
            workOrder.AssignedTechnicianUserId = null;
            workOrder.Status = WorkOrderStatus.Open;
            workOrder.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            AddActivity(db, workOrder, actor.UserId, "Unassigned", "Work order was returned to the unassigned queue.");
            AddAudit(db, actor, "WorkOrderUnassigned", "WorkOrder", workOrder.Id.ToString(), $"Unassigned {WorkOrderFormatting.DisplayNumber(workOrder.Number)}.");
            await SaveHandlingConcurrencyAsync(db, ct);
            return await MapAsync(db, workOrder, ct);
        }, cancellationToken);
    }

    public async Task<WorkOrderDetailDto> ChangeStatusAsync(TenantActor actor, Guid workOrderId, string rowVersion, WorkOrderStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Status))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["status"] = ["Status is invalid."] });
        }

        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            var workOrder = await LoadTrackedVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            var decision = WorkOrderWorkflow.Evaluate(
                workOrder.Status,
                request.Status,
                membership.Role,
                workOrder.AssignedTechnicianUserId == actor.UserId,
                workOrder.RequesterUserId == actor.UserId);
            if (!decision.Allowed)
            {
                throw new InvalidTransitionException(decision.Error ?? "Status transition is not allowed.");
            }

            if (workOrder.Status == WorkOrderStatus.OnHold && request.Status != SlaClock.ResumeStatus(workOrder))
            {
                throw new InvalidTransitionException($"Leaving hold resumes this work order to {SlaClock.ResumeStatus(workOrder)}.");
            }

            if ((workOrder.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed)
                && request.Status is WorkOrderStatus.Open or WorkOrderStatus.InProgress
                && request.Status != (workOrder.AssignedTechnicianUserId is null ? WorkOrderStatus.Open : WorkOrderStatus.InProgress))
            {
                var expected = workOrder.AssignedTechnicianUserId is null ? WorkOrderStatus.Open : WorkOrderStatus.InProgress;
                throw new InvalidTransitionException($"Reopening this work order returns it to {expected} and clears resolution timestamps. The SLA deadline is not reset.");
            }

            SetClientRowVersion(db, workOrder, rowVersion);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var previous = workOrder.Status;
            ApplyStatus(workOrder, request.Status, now);
            AddActivity(db, workOrder, actor.UserId, "StatusChanged", $"Status changed from {previous} to {workOrder.Status}.");
            AddAudit(db, actor, "WorkOrderStatusChanged", "WorkOrder", workOrder.Id.ToString(), $"{WorkOrderFormatting.DisplayNumber(workOrder.Number)} changed from {previous} to {workOrder.Status}.");
            var recipients = new List<Guid> { workOrder.RequesterUserId };
            if (workOrder.AssignedTechnicianUserId is Guid assignee)
            {
                recipients.Add(assignee);
            }

            await EnqueueAsync(db, actor.OrganizationId, "WorkOrderStatusChanged", workOrder, recipients, now, ct);
            await SaveHandlingConcurrencyAsync(db, ct);
            return await MapAsync(db, workOrder, ct);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<CommentDto>> ListCommentsAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        await LoadVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.OrganizationId == actor.OrganizationId && c.WorkOrderId == workOrderId)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, comments.Select(c => c.AuthorUserId), cancellationToken);
        return comments.Select(c => new CommentDto(c.Id, c.AuthorUserId, Name(names, c.AuthorUserId), c.Body, c.CreatedAtUtc)).ToList();
    }

    public async Task<CommentDto> AddCommentAsync(TenantActor actor, Guid workOrderId, string body, CancellationToken cancellationToken)
    {
        RequestValidation.EnsureComment(body);
        return await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            var workOrder = await LoadTrackedVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            if (!PermissionMatrix.CanComment(membership.Role, actor.UserId, workOrder.RequesterUserId, workOrder.AssignedTechnicianUserId, workOrder.Status))
            {
                throw new ForbiddenException("You cannot comment on this work order.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var comment = new WorkOrderComment
            {
                Id = Guid.NewGuid(),
                OrganizationId = actor.OrganizationId,
                WorkOrderId = workOrder.Id,
                AuthorUserId = actor.UserId,
                Body = body.Trim(),
                CreatedAtUtc = now
            };
            db.Comments.Add(comment);
            AddActivity(db, workOrder, actor.UserId, "CommentAdded", "A comment was added.");
            AddAudit(db, actor, "CommentAdded", "WorkOrder", workOrder.Id.ToString(), $"Comment added on {WorkOrderFormatting.DisplayNumber(workOrder.Number)}.");
            await db.SaveChangesAsync(ct);
            var names = await TenantAccess.NamesAsync(db, [actor.UserId], ct);
            return new CommentDto(comment.Id, actor.UserId, Name(names, actor.UserId), comment.Body, comment.CreatedAtUtc);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityDto>> ListActivityAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        await LoadVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        var rows = await db.Activities.AsNoTracking()
            .Where(a => a.OrganizationId == actor.OrganizationId && a.WorkOrderId == workOrderId)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, rows.Where(a => a.ActorUserId.HasValue).Select(a => a.ActorUserId!.Value), cancellationToken);
        return rows.Select(a => new ActivityDto(
            a.Id,
            a.ActivityType,
            a.Message,
            a.ActorUserId,
            a.ActorUserId is Guid id ? Name(names, id) : null,
            a.CreatedAtUtc)).ToList();
    }

    private async Task<IdempotentHttpResult> CreateCoreAsync(OpsFlowDbContext db, TenantActor actor, CreateWorkOrderRequest request, CancellationToken cancellationToken)
    {
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanCreateWorkOrder(membership.Role))
        {
            throw new ForbiddenException("You cannot create work orders in this organization.");
        }

        var organization = await db.Organizations.FirstAsync(o => o.Id == actor.OrganizationId, cancellationToken);
        var duration = await RequireSlaDurationAsync(db, actor.OrganizationId, request.Priority, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var number = organization.NextWorkOrderNumber + 1;
        organization.NextWorkOrderNumber = number;
        var workOrder = new WorkOrder
        {
            Id = Guid.NewGuid(),
            OrganizationId = actor.OrganizationId,
            Number = number,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Category = request.Category.Trim(),
            Priority = request.Priority,
            Status = WorkOrderStatus.Open,
            RequesterUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SlaDurationSeconds = duration,
            DueAtUtc = SlaClock.ComputeDueAt(now, duration, 0)
        };
        db.WorkOrders.Add(workOrder);
        AddActivity(db, workOrder, actor.UserId, "Created", "Work order was created.");
        AddAudit(db, actor, "WorkOrderCreated", "WorkOrder", workOrder.Id.ToString(), $"Created {WorkOrderFormatting.DisplayNumber(number)}.");
        var recipients = await db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == actor.OrganizationId && (m.Role == OrganizationRole.Admin || m.Role == OrganizationRole.Dispatcher))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        await EnqueueAsync(db, actor.OrganizationId, "WorkOrderCreated", workOrder, recipients, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var dto = await MapAsync(db, workOrder, cancellationToken);
        return new IdempotentHttpResult(201, JsonDefaults.Serialize(dto), false);
    }

    private async Task<IdempotentHttpResult> ClaimCoreAsync(OpsFlowDbContext db, TenantActor actor, Guid workOrderId, CancellationToken cancellationToken)
    {
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanClaim(membership.Role))
        {
            throw new ForbiddenException("Only a technician can claim work orders.");
        }

        var exists = await db.WorkOrders.AsNoTracking()
            .AnyAsync(w => w.Id == workOrderId && w.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException("Work order was not found.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await db.WorkOrders
            .Where(w => w.Id == workOrderId
                && w.OrganizationId == actor.OrganizationId
                && w.AssignedTechnicianUserId == null
                && w.Status == WorkOrderStatus.Open)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.AssignedTechnicianUserId, actor.UserId)
                .SetProperty(w => w.Status, WorkOrderStatus.Assigned)
                .SetProperty(w => w.UpdatedAtUtc, now), cancellationToken);

        if (updated == 0)
        {
            var problem = JsonDefaults.Serialize(new { title = "Conflict", status = 409, detail = "The work order was already claimed or is not open." });
            return new IdempotentHttpResult(409, problem, false);
        }

        var workOrder = await db.WorkOrders.FirstAsync(w => w.Id == workOrderId && w.OrganizationId == actor.OrganizationId, cancellationToken);
        AddActivity(db, workOrder, actor.UserId, "Claimed", "Work order was claimed.");
        AddAudit(db, actor, "WorkOrderClaimed", "WorkOrder", workOrder.Id.ToString(), $"Claimed {WorkOrderFormatting.DisplayNumber(workOrder.Number)}.");
        await EnqueueAsync(db, actor.OrganizationId, "WorkOrderClaimed", workOrder, [workOrder.RequesterUserId], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var dto = await MapAsync(db, workOrder, cancellationToken);
        return new IdempotentHttpResult(200, JsonDefaults.Serialize(dto), false);
    }

    private static void ApplyStatus(WorkOrder workOrder, WorkOrderStatus requested, DateTime utcNow)
    {
        if (requested == WorkOrderStatus.OnHold)
        {
            SlaClock.EnterHold(workOrder, utcNow);
            return;
        }

        if (workOrder.Status == WorkOrderStatus.OnHold)
        {
            SlaClock.ExitHold(workOrder, utcNow);
            return;
        }

        if (workOrder.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed
            && requested is WorkOrderStatus.Open or WorkOrderStatus.InProgress)
        {
            SlaClock.Reopen(workOrder, utcNow);
            return;
        }

        if (requested == WorkOrderStatus.Resolved)
        {
            SlaClock.MarkResolved(workOrder, utcNow);
            return;
        }

        if (requested == WorkOrderStatus.Closed)
        {
            SlaClock.MarkClosed(workOrder, utcNow);
            return;
        }

        if (requested == WorkOrderStatus.Cancelled)
        {
            SlaClock.MarkCancelled(workOrder, utcNow);
            return;
        }

        workOrder.Status = requested;
        workOrder.UpdatedAtUtc = utcNow;
    }

    private async Task ReauthorizeAsync(TenantActor actor, Func<OrganizationRole, bool> permission, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!permission(membership.Role))
        {
            throw new ForbiddenException();
        }
    }

    private static async Task<int> RequireSlaDurationAsync(OpsFlowDbContext db, Guid organizationId, WorkOrderPriority priority, CancellationToken cancellationToken)
    {
        var policy = await db.SlaPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.Priority == priority, cancellationToken);
        return policy?.DurationSeconds ?? throw new ConflictException($"No SLA policy is configured for {priority}.");
    }

    private static IQueryable<WorkOrder> ApplyVisibility(IQueryable<WorkOrder> query, OrganizationRole role, Guid userId)
    {
        return role switch
        {
            OrganizationRole.Requester => query.Where(w => w.RequesterUserId == userId),
            OrganizationRole.Technician => query.Where(w => w.AssignedTechnicianUserId == userId || (w.AssignedTechnicianUserId == null && w.Status == WorkOrderStatus.Open)),
            _ => query
        };
    }

    private static IQueryable<WorkOrder> ApplyFilters(IQueryable<WorkOrder> query, WorkOrderListQuery filter)
    {
        if (filter.Status is WorkOrderStatus status)
        {
            query = query.Where(w => w.Status == status);
        }

        if (filter.Priority is WorkOrderPriority priority)
        {
            query = query.Where(w => w.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim();
            query = query.Where(w => w.Category == category);
        }

        if (filter.AssigneeId is Guid assigneeId)
        {
            query = query.Where(w => w.AssignedTechnicianUserId == assigneeId);
        }

        if (filter.RequesterId is Guid requesterId)
        {
            query = query.Where(w => w.RequesterUserId == requesterId);
        }

        if (filter.CreatedFromUtc is DateTime createdFrom)
        {
            query = query.Where(w => w.CreatedAtUtc >= createdFrom);
        }

        if (filter.CreatedToUtc is DateTime createdTo)
        {
            query = query.Where(w => w.CreatedAtUtc < createdTo);
        }

        if (filter.DueFromUtc is DateTime dueFrom)
        {
            query = query.Where(w => w.DueAtUtc >= dueFrom);
        }

        if (filter.DueToUtc is DateTime dueTo)
        {
            query = query.Where(w => w.DueAtUtc < dueTo);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var numberText = term.StartsWith("WO-", StringComparison.OrdinalIgnoreCase) ? term[3..] : term;
            if (int.TryParse(numberText, out var number) && numberText.All(char.IsDigit))
            {
                query = query.Where(w => w.Number == number);
            }
            else
            {
                var pattern = $"%{SearchText.EscapeLike(term)}%";
                query = query.Where(w => EF.Functions.Like(w.Title, pattern) || EF.Functions.Like(w.Description, pattern));
            }
        }

        return query;
    }

    private static IQueryable<WorkOrder> ApplySort(IQueryable<WorkOrder> query, string? sort, string? direction)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? "createdat" : sort.Trim().ToLowerInvariant();
        var allowed = new[] { "createdat", "updatedat", "dueat", "priority", "status", "number" };
        if (!allowed.Contains(key))
        {
            throw new AppValidationException(new Dictionary<string, string[]>
            {
                ["sort"] = ["Sort must be createdAt, updatedAt, dueAt, priority, status, or number."]
            });
        }

        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        if (direction is not null && !descending && !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["direction"] = ["Direction must be asc or desc."] });
        }

        return key switch
        {
            "number" => descending ? query.OrderByDescending(w => w.Number) : query.OrderBy(w => w.Number),
            "priority" => descending
                ? query.OrderByDescending(w => w.Priority == WorkOrderPriority.Critical ? 4 : w.Priority == WorkOrderPriority.High ? 3 : w.Priority == WorkOrderPriority.Medium ? 2 : 1).ThenBy(w => w.Number)
                : query.OrderBy(w => w.Priority == WorkOrderPriority.Critical ? 4 : w.Priority == WorkOrderPriority.High ? 3 : w.Priority == WorkOrderPriority.Medium ? 2 : 1).ThenBy(w => w.Number),
            "status" => descending ? query.OrderByDescending(w => w.Status).ThenBy(w => w.Number) : query.OrderBy(w => w.Status).ThenBy(w => w.Number),
            "dueat" => descending ? query.OrderByDescending(w => w.DueAtUtc).ThenBy(w => w.Number) : query.OrderBy(w => w.DueAtUtc).ThenBy(w => w.Number),
            "updatedat" => descending ? query.OrderByDescending(w => w.UpdatedAtUtc).ThenBy(w => w.Number) : query.OrderBy(w => w.UpdatedAtUtc).ThenBy(w => w.Number),
            _ => descending ? query.OrderByDescending(w => w.CreatedAtUtc).ThenByDescending(w => w.Number) : query.OrderBy(w => w.CreatedAtUtc).ThenBy(w => w.Number)
        };
    }

    private static async Task<WorkOrder> LoadVisibleAsync(OpsFlowDbContext db, TenantActor actor, OrganizationRole role, Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workOrderId && w.OrganizationId == actor.OrganizationId, cancellationToken);
        return EnsureVisible(workOrder, role, actor.UserId);
    }

    private static async Task<WorkOrder> LoadTrackedVisibleAsync(OpsFlowDbContext db, TenantActor actor, OrganizationRole role, Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await db.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId && w.OrganizationId == actor.OrganizationId, cancellationToken);
        return EnsureVisible(workOrder, role, actor.UserId);
    }

    private static WorkOrder EnsureVisible(WorkOrder? workOrder, OrganizationRole role, Guid userId)
    {
        if (workOrder is null)
        {
            throw new NotFoundException("Work order was not found.");
        }

        if (!PermissionMatrix.CanViewWorkOrder(role, userId, workOrder.RequesterUserId, workOrder.AssignedTechnicianUserId, workOrder.Status))
        {
            throw new ForbiddenException("You cannot view this work order.");
        }

        return workOrder;
    }

    private static void SetClientRowVersion(OpsFlowDbContext db, WorkOrder workOrder, string rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            throw new PreconditionFailedException("If-Match with the work order row version is required.");
        }

        db.Entry(workOrder).Property(w => w.RowVersion).OriginalValue = WorkOrderFormatting.ParseRowVersion(rowVersion);
    }

    private static async Task SaveHandlingConcurrencyAsync(OpsFlowDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new PreconditionFailedException("The work order was updated by someone else. Refresh and try again.");
        }
    }

    private async Task<WorkOrderDetailDto> MapAsync(OpsFlowDbContext db, WorkOrder workOrder, CancellationToken cancellationToken)
    {
        await db.Entry(workOrder).ReloadAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, UserIds(workOrder), cancellationToken);
        return ToDetail(workOrder, names, timeProvider.GetUtcNow().UtcDateTime);
    }

    private static WorkOrderSummaryDto ToSummary(WorkOrder workOrder, IReadOnlyDictionary<Guid, string> names, DateTime utcNow)
        => new(
            workOrder.Id,
            workOrder.Number,
            WorkOrderFormatting.DisplayNumber(workOrder.Number),
            workOrder.Title,
            workOrder.Category,
            workOrder.Priority,
            workOrder.Status,
            workOrder.RequesterUserId,
            Name(names, workOrder.RequesterUserId),
            workOrder.AssignedTechnicianUserId,
            workOrder.AssignedTechnicianUserId is Guid assignee ? Name(names, assignee) : null,
            workOrder.CreatedAtUtc,
            workOrder.UpdatedAtUtc,
            workOrder.DueAtUtc,
            SlaClock.IsOverdue(workOrder.Status, workOrder.DueAtUtc, utcNow),
            workOrder.Status == WorkOrderStatus.OnHold,
            WorkOrderFormatting.RowVersionToken(workOrder.RowVersion));

    private static WorkOrderDetailDto ToDetail(WorkOrder workOrder, IReadOnlyDictionary<Guid, string> names, DateTime utcNow)
        => new(
            workOrder.Id,
            workOrder.Number,
            WorkOrderFormatting.DisplayNumber(workOrder.Number),
            workOrder.Title,
            workOrder.Description,
            workOrder.Category,
            workOrder.Priority,
            workOrder.Status,
            workOrder.RequesterUserId,
            Name(names, workOrder.RequesterUserId),
            workOrder.AssignedTechnicianUserId,
            workOrder.AssignedTechnicianUserId is Guid assignee ? Name(names, assignee) : null,
            workOrder.CreatedAtUtc,
            workOrder.UpdatedAtUtc,
            workOrder.DueAtUtc,
            workOrder.ResolvedAtUtc,
            workOrder.ClosedAtUtc,
            workOrder.SlaDurationSeconds,
            workOrder.HoldAccumulatedSeconds,
            SlaClock.IsOverdue(workOrder.Status, workOrder.DueAtUtc, utcNow),
            workOrder.Status == WorkOrderStatus.OnHold,
            WorkOrderFormatting.RowVersionToken(workOrder.RowVersion),
            workOrder.Status == WorkOrderStatus.OnHold ? SlaClock.ResumeStatus(workOrder) : null);

    private static IEnumerable<Guid> UserIds(WorkOrder workOrder)
    {
        yield return workOrder.RequesterUserId;
        if (workOrder.AssignedTechnicianUserId is Guid assignee)
        {
            yield return assignee;
        }
    }

    private static IEnumerable<Guid> RowUserIds(WorkOrder workOrder) => UserIds(workOrder);

    private static string Name(IReadOnlyDictionary<Guid, string> names, Guid userId)
        => names.TryGetValue(userId, out var name) ? name : "Unknown";

    private static void AddActivity(OpsFlowDbContext db, WorkOrder workOrder, Guid actorUserId, string type, string message)
    {
        db.Activities.Add(new WorkOrderActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workOrder.OrganizationId,
            WorkOrderId = workOrder.Id,
            ActorUserId = actorUserId,
            ActivityType = type,
            Message = message.Length > 500 ? message[..500] : message,
            CreatedAtUtc = workOrder.UpdatedAtUtc == default ? DateTime.UtcNow : workOrder.UpdatedAtUtc
        });
    }

    private static void AddAudit(OpsFlowDbContext db, TenantActor actor, string action, string entityType, string entityId, string summary)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actor.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary.Length > 500 ? summary[..500] : summary,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static async Task EnqueueAsync(
        OpsFlowDbContext db,
        Guid organizationId,
        string type,
        WorkOrder workOrder,
        IEnumerable<Guid> recipientUserIds,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var ids = recipientUserIds.Distinct().Take(20).ToArray();
        var emails = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.Email != null)
            .Select(u => u.Email!)
            .ToListAsync(cancellationToken);
        if (emails.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type,
            workOrderId = workOrder.Id,
            number = WorkOrderFormatting.DisplayNumber(workOrder.Number),
            title = workOrder.Title,
            recipients = emails
        }, JsonDefaults.Options);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Type = type,
            Payload = payload.Length > 4000 ? payload[..4000] : payload,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAtUtc = utcNow,
            CreatedAtUtc = utcNow,
            UpdatedAtUtc = utcNow
        });
    }
}

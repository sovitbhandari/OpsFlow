using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Processing;

public sealed class SlaEscalationScanner(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    TransactionRunner transactions,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<SlaEscalationScanner> logger) : ISlaEscalationScanner
{
    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var overdue = await db.WorkOrders.AsNoTracking()
            .Where(w => (w.Status == WorkOrderStatus.Open || w.Status == WorkOrderStatus.Assigned || w.Status == WorkOrderStatus.InProgress)
                && w.DueAtUtc < now
                && !db.SlaEscalations.Any(e => e.WorkOrderId == w.Id))
            .OrderBy(w => w.DueAtUtc)
            .Take(options.Value.SlaScanBatchSize)
            .Select(w => new { w.Id, w.OrganizationId, w.Number, w.Title })
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var workOrder in overdue)
        {
            try
            {
                var inserted = await transactions.ExecuteAsync(async (tx, ct) =>
                {
                    var stillMissing = !await tx.SlaEscalations.AnyAsync(e => e.WorkOrderId == workOrder.Id, ct);
                    if (!stillMissing)
                    {
                        return false;
                    }

                    var moment = timeProvider.GetUtcNow().UtcDateTime;
                    tx.SlaEscalations.Add(new SlaEscalation
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = workOrder.OrganizationId,
                        WorkOrderId = workOrder.Id,
                        CreatedAtUtc = moment
                    });
                    var recipients = await tx.Memberships.AsNoTracking()
                        .Where(m => m.OrganizationId == workOrder.OrganizationId && (m.Role == OrganizationRole.Admin || m.Role == OrganizationRole.Dispatcher))
                        .Select(m => m.UserId)
                        .Take(20)
                        .ToListAsync(ct);
                    var emails = await tx.Users.AsNoTracking()
                        .Where(u => recipients.Contains(u.Id) && u.Email != null)
                        .Select(u => u.Email!)
                        .ToListAsync(ct);
                    if (emails.Count > 0)
                    {
                        var payload = JsonSerializer.Serialize(new
                        {
                            type = "SlaEscalated",
                            workOrderId = workOrder.Id,
                            number = WorkOrderFormatting.DisplayNumber(workOrder.Number),
                            title = workOrder.Title,
                            recipients = emails
                        }, JsonDefaults.Options);
                        tx.OutboxMessages.Add(new OutboxMessage
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = workOrder.OrganizationId,
                            Type = "SlaEscalated",
                            Payload = payload,
                            Status = OutboxStatus.Pending,
                            NextAttemptAtUtc = moment,
                            CreatedAtUtc = moment,
                            UpdatedAtUtc = moment
                        });
                    }

                    tx.Activities.Add(new WorkOrderActivity
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = workOrder.OrganizationId,
                        WorkOrderId = workOrder.Id,
                        ActivityType = "SlaEscalated",
                        Message = "SLA elapsed and an escalation was recorded.",
                        CreatedAtUtc = moment
                    });
                    tx.AuditLogs.Add(new AuditLog
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = workOrder.OrganizationId,
                        Action = "SlaEscalated",
                        EntityType = "WorkOrder",
                        EntityId = workOrder.Id.ToString(),
                        Summary = $"Escalated {WorkOrderFormatting.DisplayNumber(workOrder.Number)} after the elapsed-time SLA.",
                        CreatedAtUtc = moment
                    });
                    await tx.SaveChangesAsync(ct);
                    return true;
                }, cancellationToken);
                if (inserted)
                {
                    created++;
                }
            }
            catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolation(exception))
            {
                logger.LogInformation("Skipped duplicate SLA escalation for work order {WorkOrderId}.", workOrder.Id);
            }
        }

        if (created > 0)
        {
            logger.LogInformation("Recorded {Count} SLA escalation events.", created);
        }

        return created;
    }
}

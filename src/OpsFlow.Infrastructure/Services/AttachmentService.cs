using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class AttachmentService(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    TransactionRunner transactions,
    IAttachmentStorage storage,
    IOptions<AttachmentOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        await RequireVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.OrganizationId == actor.OrganizationId && a.WorkOrderId == workOrderId)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var names = await TenantAccess.NamesAsync(db, rows.Select(r => r.UploadedByUserId), cancellationToken);
        return rows.Select(row => ToDto(row, names)).ToList();
    }

    public async Task<AttachmentDto> UploadAsync(TenantActor actor, Guid workOrderId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (content.CanSeek && content.Length > settings.MaxBytes)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = [$"Files must be {settings.MaxBytes} bytes or smaller."] });
        }

        await using var buffer = new MemoryStream();
        var copied = 0L;
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            copied += read;
            if (copied > settings.MaxBytes)
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = [$"Files must be {settings.MaxBytes} bytes or smaller."] });
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (copied == 0)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] });
        }

        var safeName = AttachmentRules.SanitizeFileName(fileName);
        if (!AttachmentRules.TryResolveContentType(safeName, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 64)), out var contentType, out var error))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = [error] });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        var workOrder = await RequireVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        if (!PermissionMatrix.CanUploadAttachment(membership.Role, actor.UserId, workOrder.RequesterUserId, workOrder.AssignedTechnicianUserId, workOrder.Status))
        {
            throw new ForbiddenException("You cannot add attachments to this work order.");
        }

        var count = await db.Attachments.CountAsync(a => a.OrganizationId == actor.OrganizationId && a.WorkOrderId == workOrderId, cancellationToken);
        if (count >= settings.MaxPerWorkOrder)
        {
            throw new ConflictException($"A work order can have at most {settings.MaxPerWorkOrder} attachments.");
        }

        buffer.Position = 0;
        var storageKey = await storage.SaveAsync(actor.OrganizationId, workOrderId, buffer, cancellationToken);
        try
        {
            return await transactions.ExecuteAsync(async (tx, ct) =>
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var attachment = new Attachment
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = actor.OrganizationId,
                    WorkOrderId = workOrderId,
                    FileName = safeName,
                    ContentType = contentType,
                    SizeBytes = copied,
                    StorageKey = storageKey,
                    UploadedByUserId = actor.UserId,
                    CreatedAtUtc = now
                };
                tx.Attachments.Add(attachment);
                tx.Activities.Add(new WorkOrderActivity
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = actor.OrganizationId,
                    WorkOrderId = workOrderId,
                    ActorUserId = actor.UserId,
                    ActivityType = "AttachmentAdded",
                    Message = "An attachment was added.",
                    CreatedAtUtc = now
                });
                tx.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = actor.OrganizationId,
                    ActorUserId = actor.UserId,
                    Action = "AttachmentAdded",
                    EntityType = "Attachment",
                    EntityId = attachment.Id.ToString(),
                    Summary = $"Added attachment {safeName}.",
                    CreatedAtUtc = now
                });
                await tx.SaveChangesAsync(ct);
                var names = await TenantAccess.NamesAsync(tx, [actor.UserId], ct);
                return ToDto(attachment, names);
            }, cancellationToken);
        }
        catch
        {
            try
            {
                await storage.DeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception cleanupError)
            {
                logger.LogWarning(cleanupError, "Failed to compensate attachment object {StorageKey} after a database error.", storageKey);
            }

            throw;
        }
    }

    public async Task<AttachmentDownload> OpenAsync(TenantActor actor, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        await RequireVisibleAsync(db, actor, membership.Role, workOrderId, cancellationToken);
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == attachmentId && a.WorkOrderId == workOrderId && a.OrganizationId == actor.OrganizationId, cancellationToken);
        if (attachment is null)
        {
            throw new NotFoundException("Attachment was not found.");
        }

        var stream = await storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return new AttachmentDownload(attachment.FileName, attachment.ContentType, stream);
    }

    public async Task DeleteAsync(TenantActor actor, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken)
    {
        string? storageKey = null;
        await transactions.ExecuteAsync(async (db, ct) =>
        {
            var membership = await TenantAccess.RequireMemberAsync(db, actor, ct);
            var workOrder = await RequireVisibleAsync(db, actor, membership.Role, workOrderId, ct);
            var attachment = await db.Attachments.FirstOrDefaultAsync(
                a => a.Id == attachmentId && a.WorkOrderId == workOrderId && a.OrganizationId == actor.OrganizationId, ct);
            if (attachment is null)
            {
                throw new NotFoundException("Attachment was not found.");
            }

            if (!PermissionMatrix.CanDeleteAttachment(
                membership.Role,
                actor.UserId,
                attachment.UploadedByUserId,
                PermissionMatrix.CanViewWorkOrder(membership.Role, actor.UserId, workOrder.RequesterUserId, workOrder.AssignedTechnicianUserId, workOrder.Status)))
            {
                throw new ForbiddenException("You cannot delete this attachment.");
            }

            storageKey = attachment.StorageKey;
            db.Attachments.Remove(attachment);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrganizationId = actor.OrganizationId,
                ActorUserId = actor.UserId,
                Action = "AttachmentDeleted",
                EntityType = "Attachment",
                EntityId = attachment.Id.ToString(),
                Summary = $"Deleted attachment {attachment.FileName}.",
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            return 0;
        }, cancellationToken);

        if (storageKey is not null)
        {
            try
            {
                await storage.DeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Attachment metadata was deleted but object {StorageKey} remains.", storageKey);
            }
        }
    }

    private static async Task<WorkOrder> RequireVisibleAsync(OpsFlowDbContext db, TenantActor actor, OrganizationRole role, Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workOrderId && w.OrganizationId == actor.OrganizationId, cancellationToken);
        if (workOrder is null)
        {
            throw new NotFoundException("Work order was not found.");
        }

        if (!PermissionMatrix.CanViewWorkOrder(role, actor.UserId, workOrder.RequesterUserId, workOrder.AssignedTechnicianUserId, workOrder.Status))
        {
            throw new ForbiddenException("You cannot view this work order.");
        }

        return workOrder;
    }

    private static AttachmentDto ToDto(Attachment attachment, IReadOnlyDictionary<Guid, string> names)
        => new(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.UploadedByUserId,
            names.TryGetValue(attachment.UploadedByUserId, out var name) ? name : "Unknown", attachment.CreatedAtUtc);
}

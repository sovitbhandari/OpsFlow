namespace OpsFlow.Application;

public interface IWorkOrderService
{
    Task<PagedResult<WorkOrderSummaryDto>> ListAsync(TenantActor actor, WorkOrderListQuery query, CancellationToken cancellationToken);
    Task<WorkOrderDetailDto> GetAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken);
    Task<IdempotentHttpResult> CreateAsync(TenantActor actor, CreateWorkOrderRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<IdempotentHttpResult> ClaimAsync(TenantActor actor, Guid workOrderId, string idempotencyKey, CancellationToken cancellationToken);
    Task<WorkOrderDetailDto> UpdateAsync(TenantActor actor, Guid workOrderId, string rowVersion, UpdateWorkOrderRequest request, CancellationToken cancellationToken);
    Task<WorkOrderDetailDto> AssignAsync(TenantActor actor, Guid workOrderId, string rowVersion, Guid technicianUserId, CancellationToken cancellationToken);
    Task<WorkOrderDetailDto> UnassignAsync(TenantActor actor, Guid workOrderId, string rowVersion, CancellationToken cancellationToken);
    Task<WorkOrderDetailDto> ChangeStatusAsync(TenantActor actor, Guid workOrderId, string rowVersion, WorkOrderStatusRequest status, CancellationToken cancellationToken);
    Task<IReadOnlyList<CommentDto>> ListCommentsAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken);
    Task<CommentDto> AddCommentAsync(TenantActor actor, Guid workOrderId, string body, CancellationToken cancellationToken);
    Task<IReadOnlyList<ActivityDto>> ListActivityAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken);
}

public sealed record WorkOrderStatusRequest(OpsFlow.Domain.WorkOrderStatus Status);

public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationDto>> ListMineAsync(Guid userId, CancellationToken cancellationToken);
    Task<OrganizationDto> GetAsync(TenantActor actor, CancellationToken cancellationToken);
    Task<OrganizationDto> UpdateAsync(TenantActor actor, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<SlaPolicyDto>> GetSlaPoliciesAsync(TenantActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<SlaPolicyDto>> UpdateSlaPoliciesAsync(TenantActor actor, IReadOnlyList<UpdateSlaPolicyRequest> policies, CancellationToken cancellationToken);
}

public interface IMembershipService
{
    Task<IReadOnlyList<MembershipDto>> ListAsync(TenantActor actor, CancellationToken cancellationToken);
    Task<MembershipDto> AddAsync(TenantActor actor, CreateMemberRequest request, CancellationToken cancellationToken);
    Task<MembershipDto> UpdateRoleAsync(TenantActor actor, Guid userId, OpsFlow.Domain.OrganizationRole role, CancellationToken cancellationToken);
    Task RemoveAsync(TenantActor actor, Guid userId, CancellationToken cancellationToken);
}

public interface IReportService
{
    Task<ReportSummaryDto> GetSummaryAsync(TenantActor actor, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);
}

public interface IAuditQueryService
{
    Task<PagedResult<AuditLogDto>> ListAsync(TenantActor actor, int page, int pageSize, CancellationToken cancellationToken);
}

public interface INotificationAdminService
{
    Task<PagedResult<NotificationJobDto>> ListAsync(TenantActor actor, int page, int pageSize, CancellationToken cancellationToken);
    Task<NotificationJobDto> RetryAsync(TenantActor actor, Guid jobId, CancellationToken cancellationToken);
}

public interface IAttachmentService
{
    Task<IReadOnlyList<AttachmentDto>> ListAsync(TenantActor actor, Guid workOrderId, CancellationToken cancellationToken);
    Task<AttachmentDto> UploadAsync(TenantActor actor, Guid workOrderId, string fileName, Stream content, CancellationToken cancellationToken);
    Task<AttachmentDownload> OpenAsync(TenantActor actor, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken);
    Task DeleteAsync(TenantActor actor, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken);
}

public sealed class AttachmentDownload(string fileName, string contentType, Stream content) : IAsyncDisposable
{
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public Stream Content { get; } = content;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IAttachmentStorage
{
    Task<string> SaveAsync(Guid organizationId, Guid workOrderId, Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string textBody, CancellationToken cancellationToken);
}

public interface IOutboxProcessor
{
    Task<int> ProcessBatchAsync(CancellationToken cancellationToken);
}

public interface ISlaEscalationScanner
{
    Task<int> ScanAsync(CancellationToken cancellationToken);
}

public interface IIdempotencyCleaner
{
    Task<int> CleanupAsync(CancellationToken cancellationToken);
}

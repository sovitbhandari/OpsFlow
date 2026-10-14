using OpsFlow.Domain;

namespace OpsFlow.Application;

public sealed record TenantActor(Guid UserId, Guid OrganizationId);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record MembershipDto(Guid UserId, string Email, string DisplayName, OrganizationRole Role, DateTime CreatedAtUtc);

public sealed record OrganizationDto(Guid Id, string Name, string Slug, OrganizationRole Role);

public sealed record CurrentUserDto(Guid Id, string Email, string DisplayName, IReadOnlyList<OrganizationDto> Organizations);

public sealed record SlaPolicyDto(WorkOrderPriority Priority, int DurationSeconds, DateTime UpdatedAtUtc);

public sealed record UpdateSlaPolicyRequest(WorkOrderPriority Priority, int DurationSeconds);

public sealed record CreateMemberRequest(string Email, string DisplayName, OrganizationRole Role, string? Password);

public sealed record UpdateMemberRequest(OrganizationRole Role);

public sealed record WorkOrderSummaryDto(
    Guid Id,
    int Number,
    string DisplayNumber,
    string Title,
    string Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    Guid RequesterUserId,
    string RequesterName,
    Guid? AssignedTechnicianUserId,
    string? AssigneeName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime DueAtUtc,
    bool IsOverdue,
    bool SlaPaused,
    string RowVersion);

public sealed record WorkOrderDetailDto(
    Guid Id,
    int Number,
    string DisplayNumber,
    string Title,
    string Description,
    string Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    Guid RequesterUserId,
    string RequesterName,
    Guid? AssignedTechnicianUserId,
    string? AssigneeName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime DueAtUtc,
    DateTime? ResolvedAtUtc,
    DateTime? ClosedAtUtc,
    int SlaDurationSeconds,
    long HoldAccumulatedSeconds,
    bool IsOverdue,
    bool SlaPaused,
    string RowVersion,
    WorkOrderStatus? HoldResumeStatus);

public sealed record CreateWorkOrderRequest(string Title, string Description, string Category, WorkOrderPriority Priority);

public sealed record UpdateWorkOrderRequest(string Title, string Description, string Category, WorkOrderPriority Priority);

public sealed record AssignWorkOrderRequest(Guid TechnicianUserId);

public sealed record ChangeStatusRequest(WorkOrderStatus Status);

public sealed record CreateCommentRequest(string Body);

public sealed record CommentDto(Guid Id, Guid AuthorUserId, string AuthorName, string Body, DateTime CreatedAtUtc);

public sealed record ActivityDto(Guid Id, string ActivityType, string Message, Guid? ActorUserId, string? ActorName, DateTime CreatedAtUtc);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, Guid UploadedByUserId, string UploadedByName, DateTime CreatedAtUtc);

public sealed record AuditLogDto(Guid Id, Guid? ActorUserId, string? ActorName, string Action, string EntityType, string EntityId, string Summary, DateTime CreatedAtUtc);

public sealed record ReportSummaryDto(
    int CohortCount,
    int OverdueCount,
    int ResolvedCount,
    double? AverageResolutionSeconds,
    int? MinResolutionSeconds,
    int? MaxResolutionSeconds,
    IReadOnlyList<CountByLabelDto> StatusCounts,
    IReadOnlyList<CountByLabelDto> PriorityCounts);

public sealed record CountByLabelDto(string Label, int Count);

public sealed record NotificationJobDto(
    Guid Id,
    string Type,
    OutboxStatus Status,
    int AttemptCount,
    DateTime NextAttemptAtUtc,
    DateTime? LeaseUntilUtc,
    string? LastError,
    DateTime CreatedAtUtc);

public sealed record WorkOrderListQuery(
    WorkOrderStatus? Status,
    WorkOrderPriority? Priority,
    string? Category,
    Guid? AssigneeId,
    Guid? RequesterId,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    DateTime? DueFromUtc,
    DateTime? DueToUtc,
    string? Search,
    string? Sort,
    string? Direction,
    int Page,
    int PageSize);

public sealed record IdempotentHttpResult(int StatusCode, string Body, bool Replay);

public static class WorkOrderFormatting
{
    public static string DisplayNumber(int number) => $"WO-{number}";

    public static string RowVersionToken(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static byte[] ParseRowVersion(string token)
    {
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length == 0)
            {
                throw new FormatException();
            }

            return bytes;
        }
        catch (FormatException)
        {
            throw new PreconditionFailedException("The row version token is invalid. Refresh the work order and try again.");
        }
    }
}

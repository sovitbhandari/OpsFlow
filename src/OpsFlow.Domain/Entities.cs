namespace OpsFlow.Domain;

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int NextWorkOrderNumber { get; set; } = 1000;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class OrganizationMembership
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Organization? Organization { get; set; }
}

public sealed class SlaPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public WorkOrderPriority Priority { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkOrder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public WorkOrderPriority Priority { get; set; }
    public WorkOrderStatus Status { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid? AssignedTechnicianUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? HoldStartedAtUtc { get; set; }
    public long HoldAccumulatedSeconds { get; set; }
    public int SlaDurationSeconds { get; set; }
    public WorkOrderStatus? StatusBeforeHold { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WorkOrderComment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WorkOrderActivity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActivityType { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class Attachment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";
    public Guid UploadedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public OutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SlaEscalation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public IdempotencyStatus Status { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LeaseUntilUtc { get; set; }
}

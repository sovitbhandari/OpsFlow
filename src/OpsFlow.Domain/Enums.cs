namespace OpsFlow.Domain;

public enum OrganizationRole
{
    Admin = 1,
    Dispatcher = 2,
    Technician = 3,
    Requester = 4
}

public enum WorkOrderStatus
{
    Open = 1,
    Assigned = 2,
    InProgress = 3,
    OnHold = 4,
    Resolved = 5,
    Closed = 6,
    Cancelled = 7
}

public enum WorkOrderPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum OutboxStatus
{
    Pending = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4
}

public enum IdempotencyStatus
{
    InProgress = 1,
    Completed = 2
}

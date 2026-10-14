namespace OpsFlow.Domain;

/// <summary>
/// Elapsed-time SLA. OnHold pauses the clock. Business-hour calendars are not implemented.
/// DueAtUtc excludes the current hold interval and includes only completed hold time.
/// </summary>
public static class SlaClock
{
    public static readonly IReadOnlyDictionary<WorkOrderPriority, int> DefaultDurationSeconds =
        new Dictionary<WorkOrderPriority, int>
        {
            [WorkOrderPriority.Low] = 7 * 24 * 60 * 60,
            [WorkOrderPriority.Medium] = 3 * 24 * 60 * 60,
            [WorkOrderPriority.High] = 24 * 60 * 60,
            [WorkOrderPriority.Critical] = 4 * 60 * 60
        };

    public static DateTime ComputeDueAt(DateTime createdAtUtc, int slaDurationSeconds, long holdAccumulatedSeconds)
        => createdAtUtc.AddSeconds(slaDurationSeconds + holdAccumulatedSeconds);

    public static bool IsOverdue(WorkOrderStatus status, DateTime dueAtUtc, DateTime utcNow)
    {
        if (status is WorkOrderStatus.OnHold or WorkOrderStatus.Resolved or WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)
        {
            return false;
        }

        return dueAtUtc < utcNow;
    }

    public static void EnterHold(WorkOrder workOrder, DateTime utcNow)
    {
        workOrder.StatusBeforeHold = workOrder.Status;
        workOrder.HoldStartedAtUtc = utcNow;
        workOrder.Status = WorkOrderStatus.OnHold;
        workOrder.UpdatedAtUtc = utcNow;
    }

    public static void ExitHold(WorkOrder workOrder, DateTime utcNow)
    {
        if (workOrder.HoldStartedAtUtc is DateTime started)
        {
            var held = (long)Math.Max(0, (utcNow - started).TotalSeconds);
            workOrder.HoldAccumulatedSeconds += held;
            workOrder.HoldStartedAtUtc = null;
            workOrder.DueAtUtc = ComputeDueAt(workOrder.CreatedAtUtc, workOrder.SlaDurationSeconds, workOrder.HoldAccumulatedSeconds);
        }

        var resume = workOrder.StatusBeforeHold == WorkOrderStatus.InProgress
            ? WorkOrderStatus.InProgress
            : workOrder.AssignedTechnicianUserId is null ? WorkOrderStatus.Open : WorkOrderStatus.Assigned;
        workOrder.StatusBeforeHold = null;
        workOrder.Status = resume;
        workOrder.UpdatedAtUtc = utcNow;
    }

    public static WorkOrderStatus ResumeStatus(WorkOrder workOrder)
    {
        if (workOrder.StatusBeforeHold == WorkOrderStatus.InProgress)
        {
            return WorkOrderStatus.InProgress;
        }

        return workOrder.AssignedTechnicianUserId is null ? WorkOrderStatus.Open : WorkOrderStatus.Assigned;
    }

    public static void MarkResolved(WorkOrder workOrder, DateTime utcNow)
    {
        workOrder.ResolvedAtUtc = utcNow;
        workOrder.Status = WorkOrderStatus.Resolved;
        workOrder.UpdatedAtUtc = utcNow;
    }

    public static void MarkClosed(WorkOrder workOrder, DateTime utcNow)
    {
        workOrder.ClosedAtUtc ??= utcNow;
        workOrder.Status = WorkOrderStatus.Closed;
        workOrder.UpdatedAtUtc = utcNow;
    }

    public static void MarkCancelled(WorkOrder workOrder, DateTime utcNow)
    {
        workOrder.Status = WorkOrderStatus.Cancelled;
        workOrder.UpdatedAtUtc = utcNow;
    }

    /// <summary>
    /// Reopening clears resolution and closure timestamps. The original SLA deadline is kept,
    /// so a ticket can be overdue immediately after it is reopened.
    /// </summary>
    public static void Reopen(WorkOrder workOrder, DateTime utcNow)
    {
        workOrder.ResolvedAtUtc = null;
        workOrder.ClosedAtUtc = null;
        workOrder.HoldStartedAtUtc = null;
        workOrder.StatusBeforeHold = null;
        workOrder.Status = workOrder.AssignedTechnicianUserId is null
            ? WorkOrderStatus.Open
            : WorkOrderStatus.InProgress;
        workOrder.UpdatedAtUtc = utcNow;
    }

    public static void ApplyPriority(WorkOrder workOrder, int slaDurationSeconds)
    {
        workOrder.SlaDurationSeconds = slaDurationSeconds;
        if (workOrder.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)
        {
            return;
        }

        workOrder.DueAtUtc = ComputeDueAt(workOrder.CreatedAtUtc, slaDurationSeconds, workOrder.HoldAccumulatedSeconds);
    }
}

namespace OpsFlow.Domain;

public readonly record struct TransitionDecision(bool Allowed, string? Error)
{
    public static TransitionDecision Allow() => new(true, null);
    public static TransitionDecision Reject(string error) => new(false, error);
}

public static class WorkOrderWorkflow
{
    public static TransitionDecision Evaluate(
        WorkOrderStatus from,
        WorkOrderStatus to,
        OrganizationRole role,
        bool isAssignee,
        bool isRequester)
    {
        if (from == to)
        {
            return TransitionDecision.Reject("The work order is already in that status.");
        }

        if (from == WorkOrderStatus.Cancelled)
        {
            return TransitionDecision.Reject("Cancelled work orders are terminal and cannot change status.");
        }

        var allowed = (from, to) switch
        {
            (WorkOrderStatus.Open, WorkOrderStatus.Cancelled) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Requester && isRequester),
            (WorkOrderStatus.Assigned, WorkOrderStatus.InProgress) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Technician && isAssignee),
            (WorkOrderStatus.Assigned, WorkOrderStatus.OnHold) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Technician && isAssignee),
            (WorkOrderStatus.Assigned, WorkOrderStatus.Cancelled) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher,
            (WorkOrderStatus.InProgress, WorkOrderStatus.OnHold) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Technician && isAssignee),
            (WorkOrderStatus.InProgress, WorkOrderStatus.Resolved) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Technician && isAssignee),
            (WorkOrderStatus.InProgress, WorkOrderStatus.Cancelled) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher,
            (WorkOrderStatus.OnHold, WorkOrderStatus.InProgress or WorkOrderStatus.Assigned or WorkOrderStatus.Open) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Technician && isAssignee),
            (WorkOrderStatus.Resolved, WorkOrderStatus.Closed) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher || (role == OrganizationRole.Requester && isRequester),
            (WorkOrderStatus.Resolved, WorkOrderStatus.InProgress or WorkOrderStatus.Open) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher,
            (WorkOrderStatus.Closed, WorkOrderStatus.InProgress or WorkOrderStatus.Open) =>
                role is OrganizationRole.Admin or OrganizationRole.Dispatcher,
            _ => false
        };

        return allowed
            ? TransitionDecision.Allow()
            : TransitionDecision.Reject($"Role {role} cannot transition a work order from {from} to {to}.");
    }
}

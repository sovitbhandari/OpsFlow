namespace OpsFlow.Domain;

public static class PermissionMatrix
{
    public static bool CanManageOrganization(OrganizationRole role) => role == OrganizationRole.Admin;
    public static bool CanManageMembers(OrganizationRole role) => role == OrganizationRole.Admin;
    public static bool CanManageSla(OrganizationRole role) => role == OrganizationRole.Admin;
    public static bool CanViewAudit(OrganizationRole role) => role == OrganizationRole.Admin;
    public static bool CanViewReports(OrganizationRole role) =>
        role is OrganizationRole.Admin or OrganizationRole.Dispatcher;
    public static bool CanViewAllWorkOrders(OrganizationRole role) =>
        role is OrganizationRole.Admin or OrganizationRole.Dispatcher;
    public static bool CanAssign(OrganizationRole role) =>
        role is OrganizationRole.Admin or OrganizationRole.Dispatcher;
    public static bool CanClaim(OrganizationRole role) => role == OrganizationRole.Technician;
    public static bool CanCreateWorkOrder(OrganizationRole role) =>
        role is OrganizationRole.Admin or OrganizationRole.Dispatcher or OrganizationRole.Requester;
    public static bool CanInspectNotifications(OrganizationRole role) => role == OrganizationRole.Admin;

    public static bool CanViewWorkOrder(
        OrganizationRole role,
        Guid userId,
        Guid requesterUserId,
        Guid? assignedTechnicianUserId,
        WorkOrderStatus status)
    {
        return role switch
        {
            OrganizationRole.Admin or OrganizationRole.Dispatcher => true,
            OrganizationRole.Requester => requesterUserId == userId,
            OrganizationRole.Technician => assignedTechnicianUserId == userId
                || (assignedTechnicianUserId is null && status == WorkOrderStatus.Open),
            _ => false
        };
    }

    public static bool CanEditDetails(OrganizationRole role, bool isRequester, WorkOrderStatus status)
    {
        if (status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)
        {
            return false;
        }

        if (role is OrganizationRole.Admin or OrganizationRole.Dispatcher)
        {
            return true;
        }

        return role == OrganizationRole.Requester && isRequester && status == WorkOrderStatus.Open;
    }

    public static bool CanComment(
        OrganizationRole role,
        Guid userId,
        Guid requesterUserId,
        Guid? assignedTechnicianUserId,
        WorkOrderStatus status)
        => CanViewWorkOrder(role, userId, requesterUserId, assignedTechnicianUserId, status)
           && status != WorkOrderStatus.Cancelled;

    public static bool CanUploadAttachment(
        OrganizationRole role,
        Guid userId,
        Guid requesterUserId,
        Guid? assignedTechnicianUserId,
        WorkOrderStatus status)
        => CanComment(role, userId, requesterUserId, assignedTechnicianUserId, status);

    public static bool CanDeleteAttachment(
        OrganizationRole role,
        Guid userId,
        Guid uploadedByUserId,
        bool canView)
    {
        if (!canView)
        {
            return false;
        }

        return role is OrganizationRole.Admin or OrganizationRole.Dispatcher || uploadedByUserId == userId;
    }
}

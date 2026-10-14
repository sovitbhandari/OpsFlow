using OpsFlow.Application;
using OpsFlow.Domain;

namespace OpsFlow.UnitTests;

public class WorkflowTests
{
    [Theory]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Resolved, OrganizationRole.Admin, false, false)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.InProgress, OrganizationRole.Technician, false, false)]
    [InlineData(WorkOrderStatus.Assigned, WorkOrderStatus.Resolved, OrganizationRole.Technician, true, false)]
    [InlineData(WorkOrderStatus.Cancelled, WorkOrderStatus.Open, OrganizationRole.Admin, false, false)]
    public void Rejects_invalid_transitions(WorkOrderStatus from, WorkOrderStatus to, OrganizationRole role, bool assignee, bool requester)
    {
        var decision = WorkOrderWorkflow.Evaluate(from, to, role, assignee, requester);
        Assert.False(decision.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.Error));
    }

    [Fact]
    public void Technician_can_resolve_only_when_assigned()
    {
        Assert.True(WorkOrderWorkflow.Evaluate(WorkOrderStatus.InProgress, WorkOrderStatus.Resolved, OrganizationRole.Technician, true, false).Allowed);
        Assert.False(WorkOrderWorkflow.Evaluate(WorkOrderStatus.InProgress, WorkOrderStatus.Resolved, OrganizationRole.Technician, false, false).Allowed);
    }

    [Fact]
    public void Requester_can_cancel_only_own_open_work()
    {
        Assert.True(WorkOrderWorkflow.Evaluate(WorkOrderStatus.Open, WorkOrderStatus.Cancelled, OrganizationRole.Requester, false, true).Allowed);
        Assert.False(WorkOrderWorkflow.Evaluate(WorkOrderStatus.Open, WorkOrderStatus.Cancelled, OrganizationRole.Requester, false, false).Allowed);
    }

    [Fact]
    public void Only_admin_or_dispatcher_can_reopen()
    {
        Assert.True(WorkOrderWorkflow.Evaluate(WorkOrderStatus.Closed, WorkOrderStatus.InProgress, OrganizationRole.Dispatcher, false, false).Allowed);
        Assert.False(WorkOrderWorkflow.Evaluate(WorkOrderStatus.Resolved, WorkOrderStatus.Open, OrganizationRole.Technician, true, false).Allowed);
    }
}

public class SlaClockTests
{
    [Fact]
    public void Hold_extends_due_date_and_reopen_clears_resolution_without_resetting_sla()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var workOrder = new WorkOrder
        {
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            SlaDurationSeconds = 3600,
            DueAtUtc = SlaClock.ComputeDueAt(created, 3600, 0),
            Status = WorkOrderStatus.InProgress,
            AssignedTechnicianUserId = Guid.NewGuid()
        };

        SlaClock.EnterHold(workOrder, created.AddMinutes(30));
        Assert.Equal(WorkOrderStatus.OnHold, workOrder.Status);
        Assert.False(SlaClock.IsOverdue(workOrder.Status, workOrder.DueAtUtc, created.AddHours(3)));

        SlaClock.ExitHold(workOrder, created.AddMinutes(90));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Equal(created.AddHours(1).AddMinutes(60), workOrder.DueAtUtc);
        Assert.Equal(3600, workOrder.HoldAccumulatedSeconds);

        SlaClock.MarkResolved(workOrder, created.AddHours(2));
        Assert.NotNull(workOrder.ResolvedAtUtc);
        var dueBeforeReopen = workOrder.DueAtUtc;
        SlaClock.Reopen(workOrder, created.AddHours(5));
        Assert.Null(workOrder.ResolvedAtUtc);
        Assert.Null(workOrder.ClosedAtUtc);
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Equal(dueBeforeReopen, workOrder.DueAtUtc);
        Assert.True(SlaClock.IsOverdue(workOrder.Status, workOrder.DueAtUtc, created.AddHours(5)));
    }

    [Fact]
    public void Reopen_without_assignee_returns_to_open()
    {
        var workOrder = new WorkOrder
        {
            Status = WorkOrderStatus.Resolved,
            ResolvedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            DueAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        SlaClock.Reopen(workOrder, DateTime.UtcNow);
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
    }
}

public class PermissionAndAttachmentTests
{
    [Fact]
    public void Technician_sees_unassigned_open_and_own_assignments_only()
    {
        var me = Guid.NewGuid();
        Assert.True(PermissionMatrix.CanViewWorkOrder(OrganizationRole.Technician, me, Guid.NewGuid(), null, WorkOrderStatus.Open));
        Assert.False(PermissionMatrix.CanViewWorkOrder(OrganizationRole.Technician, me, Guid.NewGuid(), null, WorkOrderStatus.Cancelled));
        Assert.False(PermissionMatrix.CanViewWorkOrder(OrganizationRole.Technician, me, Guid.NewGuid(), Guid.NewGuid(), WorkOrderStatus.Assigned));
        Assert.True(PermissionMatrix.CanClaim(OrganizationRole.Technician));
        Assert.False(PermissionMatrix.CanClaim(OrganizationRole.Dispatcher));
        Assert.False(PermissionMatrix.CanCreateWorkOrder(OrganizationRole.Technician));
    }

    [Fact]
    public void Attachment_policy_rejects_mismatched_bytes_and_path_traversal()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        Assert.True(AttachmentRules.TryResolveContentType("photo.png", png, out var contentType, out _));
        Assert.Equal("image/png", contentType);
        Assert.False(AttachmentRules.TryResolveContentType("photo.png", "%PDF"u8.ToArray(), out _, out var error));
        Assert.Contains("do not match", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(AttachmentRules.IsSafeStorageKey("../secrets"));
        Assert.Equal("file.txt", AttachmentRules.SanitizeFileName("../../file.txt"));
    }

    [Fact]
    public void Fingerprint_changes_when_the_create_payload_changes()
    {
        var left = Fingerprint.ForCreate(new CreateWorkOrderRequest("A", "B", "HVAC", WorkOrderPriority.High));
        var right = Fingerprint.ForCreate(new CreateWorkOrderRequest("A", "C", "HVAC", WorkOrderPriority.High));
        Assert.NotEqual(left, right);
        Assert.Equal(left, Fingerprint.ForCreate(new CreateWorkOrderRequest(" A ", "B", "HVAC", WorkOrderPriority.High)));
    }

    [Fact]
    public void Retry_delay_grows_and_stays_bounded()
    {
        var first = RetryBackoff.Delay(1, 1);
        var later = RetryBackoff.Delay(6, 1);
        Assert.True(later > first);
        Assert.True(RetryBackoff.Delay(20, 1.5) <= TimeSpan.FromSeconds(450));
    }

    [Fact]
    public void Validation_rejects_oversized_page_and_title()
    {
        Assert.Throws<AppValidationException>(() => RequestValidation.NormalizePaging(1, 500));
        Assert.Throws<AppValidationException>(() => RequestValidation.EnsureCreate(new CreateWorkOrderRequest("", "d", "c", WorkOrderPriority.Low)));
    }
}

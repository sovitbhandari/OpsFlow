using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Http;
using OpsFlow.Application;
using OpsFlow.Domain;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/work-orders")]
public sealed class WorkOrdersController(IWorkOrderService workOrders) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<WorkOrderSummaryDto>>> List(
        Guid organizationId,
        [FromQuery] WorkOrderStatus? status,
        [FromQuery] WorkOrderPriority? priority,
        [FromQuery] string? category,
        [FromQuery] Guid? assigneeId,
        [FromQuery] Guid? requesterId,
        [FromQuery] DateTime? createdFrom,
        [FromQuery] DateTime? createdTo,
        [FromQuery] DateTime? dueFrom,
        [FromQuery] DateTime? dueTo,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var query = new WorkOrderListQuery(status, priority, category, assigneeId, requesterId, createdFrom, createdTo, dueFrom, dueTo, search, sort, direction, page, pageSize);
        return Ok(await workOrders.ListAsync(Actor(organizationId), query, cancellationToken));
    }

    [HttpPost]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Create(
        Guid organizationId,
        [FromBody] CreateWorkOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await workOrders.CreateAsync(Actor(organizationId), request, idempotencyKey ?? "", cancellationToken);
        Response.Headers.Location = $"/api/organizations/{organizationId}/work-orders";
        return ApiResults.JsonBody(result);
    }

    [HttpGet("{workOrderId:guid}")]
    public async Task<ActionResult<WorkOrderDetailDto>> Get(Guid organizationId, Guid workOrderId, CancellationToken cancellationToken)
    {
        var detail = await workOrders.GetAsync(Actor(organizationId), workOrderId, cancellationToken);
        Response.Headers.ETag = $"\"{detail.RowVersion}\"";
        return Ok(detail);
    }

    [HttpPut("{workOrderId:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<WorkOrderDetailDto>> Update(Guid organizationId, Guid workOrderId, [FromBody] UpdateWorkOrderRequest request, CancellationToken cancellationToken)
        => Ok(await workOrders.UpdateAsync(Actor(organizationId), workOrderId, RowVersion(), request, cancellationToken));

    [HttpPost("{workOrderId:guid}/assign")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<WorkOrderDetailDto>> Assign(Guid organizationId, Guid workOrderId, [FromBody] AssignWorkOrderRequest request, CancellationToken cancellationToken)
        => Ok(await workOrders.AssignAsync(Actor(organizationId), workOrderId, RowVersion(), request.TechnicianUserId, cancellationToken));

    [HttpPost("{workOrderId:guid}/unassign")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<WorkOrderDetailDto>> Unassign(Guid organizationId, Guid workOrderId, CancellationToken cancellationToken)
        => Ok(await workOrders.UnassignAsync(Actor(organizationId), workOrderId, RowVersion(), cancellationToken));

    [HttpPost("{workOrderId:guid}/claim")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Claim(
        Guid organizationId,
        Guid workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
        => ApiResults.JsonBody(await workOrders.ClaimAsync(Actor(organizationId), workOrderId, idempotencyKey ?? "", cancellationToken));

    [HttpPost("{workOrderId:guid}/status")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<WorkOrderDetailDto>> Status(Guid organizationId, Guid workOrderId, [FromBody] ChangeStatusRequest request, CancellationToken cancellationToken)
        => Ok(await workOrders.ChangeStatusAsync(Actor(organizationId), workOrderId, RowVersion(), new WorkOrderStatusRequest(request.Status), cancellationToken));

    [HttpGet("{workOrderId:guid}/comments")]
    public async Task<ActionResult<IReadOnlyList<CommentDto>>> Comments(Guid organizationId, Guid workOrderId, CancellationToken cancellationToken)
        => Ok(await workOrders.ListCommentsAsync(Actor(organizationId), workOrderId, cancellationToken));

    [HttpPost("{workOrderId:guid}/comments")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<CommentDto>> AddComment(Guid organizationId, Guid workOrderId, [FromBody] CreateCommentRequest request, CancellationToken cancellationToken)
        => Ok(await workOrders.AddCommentAsync(Actor(organizationId), workOrderId, request.Body, cancellationToken));

    [HttpGet("{workOrderId:guid}/activity")]
    public async Task<ActionResult<IReadOnlyList<ActivityDto>>> Activity(Guid organizationId, Guid workOrderId, CancellationToken cancellationToken)
        => Ok(await workOrders.ListActivityAsync(Actor(organizationId), workOrderId, cancellationToken));

    private TenantActor Actor(Guid organizationId) => new(ApiResults.RequireUserId(User), organizationId);
    private string RowVersion() => ApiResults.RowVersion(Request) ?? "";
}

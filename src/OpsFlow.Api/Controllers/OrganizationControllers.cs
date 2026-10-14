using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Http;
using OpsFlow.Application;
using OpsFlow.Domain;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}")]
public sealed class OrganizationsController(IOrganizationService organizations) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<OrganizationDto>> Get(Guid organizationId, CancellationToken cancellationToken)
        => Ok(await organizations.GetAsync(Actor(organizationId), cancellationToken));

    [HttpPut]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<OrganizationDto>> Update(Guid organizationId, [FromBody] UpdateOrganizationRequest request, CancellationToken cancellationToken)
        => Ok(await organizations.UpdateAsync(Actor(organizationId), request.Name, cancellationToken));

    [HttpGet("sla-policies")]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> Sla(Guid organizationId, CancellationToken cancellationToken)
        => Ok(await organizations.GetSlaPoliciesAsync(Actor(organizationId), cancellationToken));

    [HttpPut("sla-policies")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> UpdateSla(Guid organizationId, [FromBody] UpdateSlaRequest request, CancellationToken cancellationToken)
        => Ok(await organizations.UpdateSlaPoliciesAsync(Actor(organizationId), request.Policies, cancellationToken));

    private TenantActor Actor(Guid organizationId) => new(ApiResults.RequireUserId(User), organizationId);

    public sealed record UpdateOrganizationRequest(string Name);
    public sealed record UpdateSlaRequest(IReadOnlyList<UpdateSlaPolicyRequest> Policies);
}

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/members")]
public sealed class MembersController(IMembershipService memberships) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MembershipDto>>> List(Guid organizationId, CancellationToken cancellationToken)
        => Ok(await memberships.ListAsync(Actor(organizationId), cancellationToken));

    [HttpPost]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<MembershipDto>> Add(Guid organizationId, [FromBody] CreateMemberRequest request, CancellationToken cancellationToken)
    {
        var created = await memberships.AddAsync(Actor(organizationId), request, cancellationToken);
        return Created($"/api/organizations/{organizationId}/members/{created.UserId}", created);
    }

    [HttpPut("{userId:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<MembershipDto>> UpdateRole(Guid organizationId, Guid userId, [FromBody] UpdateMemberRequest request, CancellationToken cancellationToken)
        => Ok(await memberships.UpdateRoleAsync(Actor(organizationId), userId, request.Role, cancellationToken));

    [HttpDelete("{userId:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Remove(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await memberships.RemoveAsync(Actor(organizationId), userId, cancellationToken);
        return NoContent();
    }

    private TenantActor Actor(Guid organizationId) => new(ApiResults.RequireUserId(User), organizationId);
}

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/reports")]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<ReportSummaryDto>> Summary(Guid organizationId, [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken cancellationToken)
        => Ok(await reports.GetSummaryAsync(new TenantActor(ApiResults.RequireUserId(User), organizationId), from, to, cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/audit")]
public sealed class AuditController(IAuditQueryService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> List(Guid organizationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await audit.ListAsync(new TenantActor(ApiResults.RequireUserId(User), organizationId), page, pageSize, cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/notification-jobs")]
public sealed class NotificationJobsController(INotificationAdminService notifications) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationJobDto>>> List(Guid organizationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await notifications.ListAsync(new TenantActor(ApiResults.RequireUserId(User), organizationId), page, pageSize, cancellationToken));

    [HttpPost("{jobId:guid}/retry")]
    [EnableRateLimiting("writes")]
    public async Task<ActionResult<NotificationJobDto>> Retry(Guid organizationId, Guid jobId, CancellationToken cancellationToken)
        => Ok(await notifications.RetryAsync(new TenantActor(ApiResults.RequireUserId(User), organizationId), jobId, cancellationToken));
}

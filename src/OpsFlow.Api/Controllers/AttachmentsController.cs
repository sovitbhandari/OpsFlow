using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Http;
using OpsFlow.Application;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/work-orders/{workOrderId:guid}/attachments")]
public sealed class AttachmentsController(IAttachmentService attachments) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(Guid organizationId, Guid workOrderId, CancellationToken cancellationToken)
        => Ok(await attachments.ListAsync(Actor(organizationId), workOrderId, cancellationToken));

    [HttpPost]
    [EnableRateLimiting("writes")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<ActionResult<AttachmentDto>> Upload(Guid organizationId, Guid workOrderId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Problem(title: "File is required", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var created = await attachments.UploadAsync(Actor(organizationId), workOrderId, file.FileName, stream, cancellationToken);
        return Created(string.Empty, created);
    }

    [HttpGet("{attachmentId:guid}")]
    public async Task<IActionResult> Download(Guid organizationId, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var download = await attachments.OpenAsync(Actor(organizationId), workOrderId, attachmentId, cancellationToken);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{download.FileName}\"";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(download.Content, download.ContentType, download.FileName);
    }

    [HttpDelete("{attachmentId:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Delete(Guid organizationId, Guid workOrderId, Guid attachmentId, CancellationToken cancellationToken)
    {
        await attachments.DeleteAsync(Actor(organizationId), workOrderId, attachmentId, cancellationToken);
        return NoContent();
    }

    private TenantActor Actor(Guid organizationId) => new(ApiResults.RequireUserId(User), organizationId);
}

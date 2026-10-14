using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application;

namespace OpsFlow.Api.Http;

public sealed class ApiExceptionHandler(IHostEnvironment environment, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            AppValidationException => (StatusCodes.Status400BadRequest, "One or more validation errors occurred."),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            PreconditionFailedException => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            InvalidTransitionException => (StatusCodes.Status400BadRequest, "Invalid status transition"),
            AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "Antiforgery validation failed"),
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception. Trace {TraceId}.", httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogInformation("Request failed with {Status}. Trace {TraceId}.", status, httpContext.TraceIdentifier);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError && !environment.IsDevelopment()
                ? "The request could not be completed."
                : exception.Message,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (exception is AppValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

public static class ApiResults
{
    public static Guid RequireUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var userId))
        {
            throw new ForbiddenException("The authentication session is missing a user id.");
        }

        return userId;
    }

    public static string? RowVersion(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("If-Match", out var value))
        {
            return null;
        }

        return value.ToString().Trim().Trim('"');
    }

    public static ContentResult JsonBody(IdempotentHttpResult result)
        => new()
        {
            StatusCode = result.StatusCode,
            Content = result.Body,
            ContentType = "application/json"
        };
}

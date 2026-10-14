namespace OpsFlow.Api.Http;

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : context.TraceIdentifier;
        context.Response.Headers[HeaderName] = correlationId;
        using (context.RequestServices.GetRequiredService<ILogger<CorrelationMiddleware>>()
            .BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

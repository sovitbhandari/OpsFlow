using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpsFlow.Api.Http;
using OpsFlow.Infrastructure;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Services.AddOpsFlowInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "OpsFlow.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("OpsFlow");
var dataProtectionOptions = builder.Configuration.GetSection(OpsFlow.Infrastructure.Options.DataProtectionOptions.SectionName).Get<OpsFlow.Infrastructure.Options.DataProtectionOptions>() ?? new OpsFlow.Infrastructure.Options.DataProtectionOptions();
if (!string.IsNullOrWhiteSpace(dataProtectionOptions.BlobUri))
{
    dataProtection.PersistKeysToAzureBlobStorage(new BlobClient(new Uri(dataProtectionOptions.BlobUri), new DefaultAzureCredential()));
}
else
{
    var keysPath = Path.GetFullPath(dataProtectionOptions.KeysPath);
    Directory.CreateDirectory(keysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "OpsFlow.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.AddScoped<RequireAntiforgeryFilter>();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<RequireAntiforgeryFilter>();
}).AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OpsFlowDbContext>("database", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var loginLimit = builder.Configuration.GetValue("RateLimiting:LoginPermitLimit", 10);
    var writeLimit = builder.Configuration.GetValue("RateLimiting:WritePermitLimit", 120);
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("writes", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = writeLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var otlp = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OpsFlow.Api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlp));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlp));
        }
    });

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
    await db.Database.MigrateAsync();
    return;
}

if (args.Contains("--seed"))
{
    if (!app.Environment.IsDevelopment())
    {
        app.Logger.LogError("Refusing to seed because ASPNETCORE_ENVIRONMENT is not Development.");
        return;
    }

    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync(CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.XFrameOptions = "DENY";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("OpsFlow API"));
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

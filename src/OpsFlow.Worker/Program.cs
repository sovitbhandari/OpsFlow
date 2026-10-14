using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpsFlow.Infrastructure;
using OpsFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddJsonConsole();
builder.Services.AddOpsFlowInfrastructure(builder.Configuration);
builder.Services.AddHostedService<OpsFlowWorkerService>();
var otlp = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OpsFlow.Worker"))
    .WithTracing(tracing =>
    {
        tracing.AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlp));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlp));
        }
    });

var host = builder.Build();
host.Run();

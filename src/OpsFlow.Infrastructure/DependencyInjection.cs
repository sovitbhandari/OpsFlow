using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application;
using OpsFlow.Infrastructure.Email;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Processing;
using OpsFlow.Infrastructure.Seeding;
using OpsFlow.Infrastructure.Services;
using OpsFlow.Infrastructure.Storage;

namespace OpsFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpsFlowInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("OpsFlow")
            ?? throw new InvalidOperationException("ConnectionStrings:OpsFlow is required.");

        services.AddDbContextFactory<OpsFlowDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(8), null);
                sql.MigrationsAssembly(typeof(OpsFlowDbContext).Assembly.GetName().Name);
                sql.CommandTimeout(60);
            });
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContext());

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<OpsFlowDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.Configure<AttachmentOptions>(configuration.GetSection(AttachmentOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<DataProtectionOptions>(configuration.GetSection(DataProtectionOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TransactionRunner>();
        services.AddScoped<IdempotencyCoordinator>();
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IMembershipService, MembershipService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<INotificationAdminService, NotificationAdminService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddScoped<ISlaEscalationScanner, SlaEscalationScanner>();
        services.AddScoped<IIdempotencyCleaner, IdempotencyCleaner>();
        services.AddScoped<DevelopmentSeeder>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        var attachmentProvider = configuration[$"{AttachmentOptions.SectionName}:Provider"] ?? "Local";
        if (string.Equals(attachmentProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAttachmentStorage, BlobAttachmentStorage>();
        }
        else
        {
            services.AddSingleton<IAttachmentStorage, LocalAttachmentStorage>();
        }

        return services;
    }
}

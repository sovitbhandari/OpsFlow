using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Processing;
using Testcontainers.MsSql;

namespace OpsFlow.WorkerTests;

public class WorkerBehaviorTests : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private ServiceProvider _services = null!;
    private MutableClock _clock = null!;
    private FakeEmailSender _email = null!;
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        _connectionString = await ResolveAsync();
        _clock = new MutableClock(DateTimeOffset.Parse("2026-04-01T00:00:00Z"));
        _email = new FakeEmailSender();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<OpsFlowDbContext>(options => options.UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<TransactionRunner>();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IEmailSender>(_email);
        services.Configure<OpsFlow.Infrastructure.Options.WorkerOptions>(options =>
        {
            options.MaxAttempts = 2;
            options.OutboxBatchSize = 10;
            options.LeaseSeconds = 30;
            options.SlaScanBatchSize = 20;
        });
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddScoped<ISlaEscalationScanner, SlaEscalationScanner>();
        _services = services.BuildServiceProvider();
        await using var db = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM IdempotencyRecords;
            DELETE FROM OutboxMessages;
            DELETE FROM AuditLogs;
            DELETE FROM SlaEscalations;
            DELETE FROM Attachments;
            DELETE FROM WorkOrderActivities;
            DELETE FROM WorkOrderComments;
            DELETE FROM WorkOrders;
            DELETE FROM SlaPolicies;
            DELETE FROM OrganizationMemberships;
            DELETE FROM Organizations;
            DELETE FROM AspNetUsers;
            """);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retries_then_dead_letters_and_recovers_expired_leases()
    {
        var organizationId = await SeedOrganizationAsync();
        await using var db = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        var jobId = Guid.NewGuid();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = jobId,
            OrganizationId = organizationId,
            Type = "WorkOrderCreated",
            Payload = """{"type":"WorkOrderCreated","workOrderId":"00000000-0000-0000-0000-000000000001","number":"WO-1","title":"Pump","recipients":["tech@opsflow.example"]}""",
            Status = OutboxStatus.Pending,
            NextAttemptAtUtc = _clock.GetUtcNow().UtcDateTime,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime,
            UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();

        _email.FailuresRemaining = 1;
        await ProcessAsync();
        var afterFirst = await ReloadAsync(jobId);
        Assert.Equal(OutboxStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.AttemptCount);
        Assert.False(string.IsNullOrWhiteSpace(afterFirst.LastError));

        _clock.Utc = _clock.Utc.AddMinutes(10);
        _email.FailuresRemaining = 1;
        await ProcessAsync();
        var dead = await ReloadAsync(jobId);
        Assert.Equal(OutboxStatus.Failed, dead.Status);
        Assert.Equal(2, dead.AttemptCount);
        Assert.Empty(_email.Sent);

        dead.Status = OutboxStatus.Processing;
        dead.LeaseUntilUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(-5);
        dead.NextAttemptAtUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await using var update = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        update.OutboxMessages.Update(dead);
        await update.SaveChangesAsync();
        _email.FailuresRemaining = 0;
        await ProcessAsync();
        var recovered = await ReloadAsync(jobId);
        Assert.Equal(OutboxStatus.Succeeded, recovered.Status);
        Assert.Contains("tech@opsflow.example", _email.Sent);
    }

    [Fact]
    public async Task Sla_scan_does_not_duplicate_escalations()
    {
        var organizationId = await SeedOrganizationAsync();
        await using var db = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync();
        db.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = user.Id,
            Role = OrganizationRole.Admin,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
        });
        var workOrder = new WorkOrder
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Number = 1001,
            Title = "Overdue",
            Description = "Late",
            Category = "HVAC",
            Priority = WorkOrderPriority.Critical,
            Status = WorkOrderStatus.Open,
            RequesterUserId = user.Id,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime.AddHours(-5),
            UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime.AddHours(-5),
            DueAtUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1),
            SlaDurationSeconds = 60
        };
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();

        await using var firstScope = _services.CreateAsyncScope();
        await firstScope.ServiceProvider.GetRequiredService<ISlaEscalationScanner>().ScanAsync(CancellationToken.None);
        await using var secondScope = _services.CreateAsyncScope();
        await secondScope.ServiceProvider.GetRequiredService<ISlaEscalationScanner>().ScanAsync(CancellationToken.None);

        await using var verify = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await verify.SlaEscalations.CountAsync(e => e.WorkOrderId == workOrder.Id));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m => m.Type == "SlaEscalated"));
    }

    private async Task ProcessAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync(CancellationToken.None);
    }

    private async Task<OutboxMessage> ReloadAsync(Guid id)
    {
        await using var db = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task<Guid> SeedOrganizationAsync()
    {
        await using var db = await _services.GetRequiredService<IDbContextFactory<OpsFlowDbContext>>().CreateDbContextAsync();
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Worker Org",
            Slug = "worker-" + Guid.NewGuid().ToString("N")[..8],
            NextWorkOrderNumber = 1000,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"admin.{organization.Slug}@opsflow.example",
            NormalizedUserName = $"ADMIN.{organization.Slug}@OPSFLOW.EXAMPLE",
            Email = $"admin.{organization.Slug}@opsflow.example",
            NormalizedEmail = $"ADMIN.{organization.Slug}@OPSFLOW.EXAMPLE",
            EmailConfirmed = true,
            DisplayName = "Admin",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
        };
        db.Organizations.Add(organization);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return organization.Id;
    }

    private async Task<string> ResolveAsync()
    {
        var external = Environment.GetEnvironmentVariable("OPSFLOW_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return RequireName(external);
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("OPSFLOW_USE_TESTCONTAINERS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Worker SQL tests require OPSFLOW_USE_TESTCONTAINERS=true or OPSFLOW_TEST_CONNECTION pointing at a database whose name contains OpsFlowTests.");
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        var master = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "master", TrustServerCertificate = true, Encrypt = true };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "IF DB_ID(N'OpsFlowTests') IS NULL CREATE DATABASE OpsFlowTests;";
        await command.ExecuteNonQueryAsync();
        master.InitialCatalog = "OpsFlowTests";
        return RequireName(master.ConnectionString);
    }

    private static string RequireName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.Contains("OpsFlowTests", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to run destructive worker tests because the database name does not contain OpsFlowTests.");
        }

        return builder.ConnectionString;
    }

    private sealed class MutableClock(DateTimeOffset utc) : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public int FailuresRemaining { get; set; }
        public List<string> Sent { get; } = [];

        public Task SendAsync(string toAddress, string subject, string textBody, CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("smtp unavailable");
            }

            Sent.Add(toAddress);
            return Task.CompletedTask;
        }
    }
}

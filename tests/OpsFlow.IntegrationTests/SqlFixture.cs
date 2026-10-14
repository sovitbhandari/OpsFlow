using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace OpsFlow.IntegrationTests;

public sealed class SqlFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ConnectionString = await ResolveConnectionStringAsync();
        var temp = Path.Combine(Path.GetTempPath(), "opsflow-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("ConnectionStrings__OpsFlow", ConnectionString);
        Environment.SetEnvironmentVariable("Attachments__Provider", "Local");
        Environment.SetEnvironmentVariable("Attachments__LocalPath", Path.Combine(temp, "files"));
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", Path.Combine(temp, "keys"));
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("RateLimiting__LoginPermitLimit", "1000");
        Environment.SetEnvironmentVariable("RateLimiting__WritePermitLimit", "1000");
        Factory = new WebApplicationFactory<Program>();
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        await db.Database.MigrateAsync();
        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task ResetAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
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
            DELETE FROM AspNetUserTokens;
            DELETE FROM AspNetUserLogins;
            DELETE FROM AspNetUserClaims;
            DELETE FROM AspNetUserRoles;
            DELETE FROM AspNetUsers;
            """);
    }

    public async Task<TestTenant> SeedAsync(string slug)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTime.UtcNow;
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            NextWorkOrderNumber = 1000,
            CreatedAtUtc = now
        };
        db.Organizations.Add(organization);
        foreach (var priority in Enum.GetValues<WorkOrderPriority>())
        {
            db.SlaPolicies.Add(new SlaPolicy
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                Priority = priority,
                DurationSeconds = SlaClock.DefaultDurationSeconds[priority],
                UpdatedAtUtc = now
            });
        }

        async Task<Guid> User(string roleName, OrganizationRole role)
        {
            var email = $"{roleName}.{slug}@opsflow.example";
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = roleName,
                CreatedAtUtc = now
            };
            var result = await userManager.CreateAsync(user, "Local-Test-Passw0rd!");
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(string.Join(' ', result.Errors.Select(error => error.Description)));
            }

            db.Memberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                UserId = user.Id,
                Role = role,
                CreatedAtUtc = now
            });
            return user.Id;
        }

        var admin = await User("admin", OrganizationRole.Admin);
        var dispatcher = await User("dispatcher", OrganizationRole.Dispatcher);
        var technicianA = await User("tech-a", OrganizationRole.Technician);
        var technicianB = await User("tech-b", OrganizationRole.Technician);
        var requester = await User("requester", OrganizationRole.Requester);
        await db.SaveChangesAsync();
        return new TestTenant(organization.Id, admin, dispatcher, technicianA, technicianB, requester, slug);
    }

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    public async Task<AuthedClient> LoginAsync(string email)
    {
        var client = CreateClient();
        await ApplyCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Local-Test-Passw0rd!" }, JsonOptions());
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Login failed for {email}: {(int)response.StatusCode} {body}");
        }

        await ApplyCsrfAsync(client);
        return new AuthedClient(client);
    }

    private static async Task ApplyCsrfAsync(HttpClient client)
    {
        var csrfResponse = await client.GetAsync("/api/auth/csrf");
        var csrfBody = await csrfResponse.Content.ReadAsStringAsync();
        if (!csrfResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"CSRF failed: {(int)csrfResponse.StatusCode} {csrfBody}");
        }

        var csrf = System.Text.Json.JsonSerializer.Deserialize<CsrfResponse>(csrfBody, JsonOptions());
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", csrf!.Token);
    }

    private async Task<string> ResolveConnectionStringAsync()
    {
        var external = Environment.GetEnvironmentVariable("OPSFLOW_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return RequireTestDatabase(external);
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("OPSFLOW_USE_TESTCONTAINERS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SQL Server integration tests are not skipped. Set OPSFLOW_USE_TESTCONTAINERS=true to start SQL Server 2022 with Testcontainers, or set OPSFLOW_TEST_CONNECTION to a dedicated database whose name contains OpsFlowTests. Azure SQL Edge, LocalDB, SQLite, PostgreSQL, and EF InMemory are not accepted.");
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
        return RequireTestDatabase(master.ConnectionString);
    }

    private static string RequireTestDatabase(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.InitialCatalog.Contains("OpsFlowTests", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException("Refusing to run destructive tests because the database name does not contain OpsFlowTests.");
        }

        return builder.ConnectionString;
    }

    public static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    private sealed record CsrfResponse(string Token);
}

public sealed record TestTenant(Guid OrganizationId, Guid AdminId, Guid DispatcherId, Guid TechnicianAId, Guid TechnicianBId, Guid RequesterId, string Slug);

public sealed class AuthedClient(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? ifMatch = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SqlFixture.JsonOptions());
        }

        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await Client.SendAsync(request);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>
{
    public const string Name = "sql";
}

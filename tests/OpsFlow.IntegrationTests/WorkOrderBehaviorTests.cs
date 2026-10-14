using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
public class SecurityTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_and_mutations_require_antiforgery()
    {
        var tenant = await fixture.SeedAsync("security");
        var anonymous = fixture.CreateClient();
        var hidden = await anonymous.GetAsync($"/api/organizations/{tenant.OrganizationId}/work-orders");
        Assert.Equal(HttpStatusCode.Unauthorized, hidden.StatusCode);

        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = $"requester.{tenant.Slug}@opsflow.example", password = "Local-Test-Passw0rd!" })
        };
        var missingToken = await anonymous.SendAsync(login);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_resources_are_not_found_and_in_org_role_violations_are_forbidden()
    {
        var north = await fixture.SeedAsync("north");
        var south = await fixture.SeedAsync("south");
        var northRequester = await fixture.LoginAsync($"requester.{north.Slug}@opsflow.example");
        var southTech = await fixture.LoginAsync($"tech-a.{south.Slug}@opsflow.example");
        var created = await CreateWorkOrderAsync(northRequester, north.OrganizationId, "Shared door sticks");
        var id = created.GetProperty("id").GetGuid();

        var crossOrg = await southTech.SendAsync(HttpMethod.Get, $"/api/organizations/{north.OrganizationId}/work-orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, crossOrg.StatusCode);

        var crossList = await southTech.SendAsync(HttpMethod.Get, $"/api/organizations/{north.OrganizationId}/work-orders");
        Assert.Equal(HttpStatusCode.NotFound, crossList.StatusCode);

        var otherAttachment = await southTech.SendAsync(HttpMethod.Get, $"/api/organizations/{north.OrganizationId}/work-orders/{id}/attachments");
        Assert.Equal(HttpStatusCode.NotFound, otherAttachment.StatusCode);

        var technician = await fixture.LoginAsync($"tech-a.{north.Slug}@opsflow.example");
        var forbiddenCreate = await technician.SendAsync(HttpMethod.Post, $"/api/organizations/{north.OrganizationId}/work-orders", new { title = "Nope", description = "x", category = "HVAC", priority = "Low" }, idempotencyKey: "technician-create-01");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);

        var audit = await northRequester.SendAsync(HttpMethod.Get, $"/api/organizations/{north.OrganizationId}/audit");
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
    }

    [Fact]
    public async Task Invalid_upload_is_rejected_and_authorized_download_uses_attachment_disposition()
    {
        var tenant = await fixture.SeedAsync("files");
        var requester = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var created = await CreateWorkOrderAsync(requester, tenant.OrganizationId, "Attach a photo");
        var id = created.GetProperty("id").GetGuid();
        var bad = new MultipartFormDataContent();
        bad.Add(new ByteArrayContent([0x00, 0x01, 0x02, 0x03]), "file", "notes.txt");
        var rejected = await requester.Client.PostAsync($"/api/organizations/{tenant.OrganizationId}/work-orders/{id}/attachments", bad);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var good = new MultipartFormDataContent();
        good.Add(new ByteArrayContent("meter reading,12\n"u8.ToArray()), "file", "meter.csv");
        var uploaded = await requester.Client.PostAsync($"/api/organizations/{tenant.OrganizationId}/work-orders/{id}/attachments", good);
        var uploadBody = await uploaded.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachmentId = JsonDocument.Parse(uploadBody).RootElement.GetProperty("id").GetGuid();

        var stranger = await fixture.LoginAsync($"requester.{(await fixture.SeedAsync("files-b")).Slug}@opsflow.example");
        var hidden = await stranger.SendAsync(HttpMethod.Get, $"/api/organizations/{tenant.OrganizationId}/work-orders/{id}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var download = await requester.SendAsync(HttpMethod.Get, $"/api/organizations/{tenant.OrganizationId}/work-orders/{id}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition?.DispositionType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("meter reading,12\n", await download.Content.ReadAsStringAsync());
    }

    internal static async Task<JsonElement> CreateWorkOrderAsync(AuthedClient client, Guid organizationId, string title)
    {
        var response = await client.SendAsync(
            HttpMethod.Post,
            $"/api/organizations/{organizationId}/work-orders",
            new { title, description = "Integration coverage", category = "HVAC", priority = "High" },
            idempotencyKey: Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

[Collection(SqlCollection.Name)]
public class ConcurrencyAndIdempotencyTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_claims_have_exactly_one_winner()
    {
        var tenant = await fixture.SeedAsync("claims");
        var requester = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var created = await SecurityTests.CreateWorkOrderAsync(requester, tenant.OrganizationId, "Unassigned pump");
        var id = created.GetProperty("id").GetGuid();
        var techA = await fixture.LoginAsync($"tech-a.{tenant.Slug}@opsflow.example");
        var techB = await fixture.LoginAsync($"tech-b.{tenant.Slug}@opsflow.example");
        var barrier = new Barrier(2);
        var url = $"/api/organizations/{tenant.OrganizationId}/work-orders/{id}/claim";
        var results = await Task.WhenAll(
            ClaimAsync(techA, url, barrier),
            ClaimAsync(techB, url, barrier));

        Assert.Equal(1, results.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(status => status == HttpStatusCode.Conflict));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var stored = await db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == id);
        Assert.Equal(WorkOrderStatus.Assigned, stored.Status);
        Assert.NotNull(stored.AssignedTechnicianUserId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "WorkOrderClaimed"));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "WorkOrderClaimed" && m.OrganizationId == tenant.OrganizationId));
    }

    [Fact]
    public async Task Stale_row_version_is_rejected()
    {
        var tenant = await fixture.SeedAsync("stale");
        var requester = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var created = await SecurityTests.CreateWorkOrderAsync(requester, tenant.OrganizationId, "Original title");
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("rowVersion").GetString();
        var first = await requester.SendAsync(HttpMethod.Put, $"/api/organizations/{tenant.OrganizationId}/work-orders/{id}", new { title = "Updated title", description = "Integration coverage", category = "HVAC", priority = "High" }, version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stale = await requester.SendAsync(HttpMethod.Put, $"/api/organizations/{tenant.OrganizationId}/work-orders/{id}", new { title = "Overwrite", description = "Integration coverage", category = "HVAC", priority = "High" }, version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Idempotency_replays_the_same_request_and_rejects_a_different_payload()
    {
        var tenant = await fixture.SeedAsync("idem");
        var requester = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var url = $"/api/organizations/{tenant.OrganizationId}/work-orders";
        var key = "create-work-order-key-01";
        var first = await requester.SendAsync(HttpMethod.Post, url, new { title = "Same", description = "One", category = "HVAC", priority = "Low" }, idempotencyKey: key);
        var replay = await requester.SendAsync(HttpMethod.Post, url, new { title = "Same", description = "One", category = "HVAC", priority = "Low" }, idempotencyKey: key);
        var conflict = await requester.SendAsync(HttpMethod.Post, url, new { title = "Different", description = "One", category = "HVAC", priority = "Low" }, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var replayId = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(firstId, replayId);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(1, await db.WorkOrders.CountAsync(w => w.OrganizationId == tenant.OrganizationId));
    }

    [Fact]
    public async Task Concurrent_duplicate_creates_do_not_double_apply()
    {
        var tenant = await fixture.SeedAsync("dupes");
        var left = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var right = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        var url = $"/api/organizations/{tenant.OrganizationId}/work-orders";
        var body = new { title = "Once", description = "Race", category = "HVAC", priority = "Medium" };
        var barrier = new Barrier(2);
        var responses = await Task.WhenAll(
            SendAsync(left, url, body, barrier),
            SendAsync(right, url, body, barrier));
        Assert.All(responses, status => Assert.True(status is HttpStatusCode.Created or HttpStatusCode.Conflict));
        Assert.Contains(HttpStatusCode.Created, responses);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(1, await db.WorkOrders.CountAsync(w => w.OrganizationId == tenant.OrganizationId && w.Title == "Once"));
    }

    [Fact]
    public async Task Failed_transaction_does_not_leave_audit_or_outbox_rows()
    {
        var tenant = await fixture.SeedAsync("rollback");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<TransactionRunner>();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var beforeAudit = await db.AuditLogs.CountAsync();
        var beforeOutbox = await db.OutboxMessages.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync<int>(async (tx, ct) =>
        {
            tx.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrganizationId = tenant.OrganizationId,
                Action = "ShouldRollback",
                EntityType = "WorkOrder",
                EntityId = Guid.NewGuid().ToString(),
                Summary = "partial",
                CreatedAtUtc = DateTime.UtcNow
            });
            tx.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OrganizationId = tenant.OrganizationId,
                Type = "ShouldRollback",
                Payload = "{}",
                Status = OutboxStatus.Pending,
                NextAttemptAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await tx.SaveChangesAsync(ct);
            throw new InvalidOperationException("force rollback");
#pragma warning disable CS0162
            return 0;
#pragma warning restore CS0162
        }, CancellationToken.None));

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var fresh = verify.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(beforeAudit, await fresh.AuditLogs.CountAsync(a => a.Action == "ShouldRollback"));
        Assert.Equal(beforeOutbox, await fresh.OutboxMessages.CountAsync(m => m.Type == "ShouldRollback"));
    }

    [Fact]
    public async Task Report_procedure_filters_by_organization_and_date()
    {
        var tenant = await fixture.SeedAsync("reports");
        var other = await fixture.SeedAsync("reports-other");
        var requester = await fixture.LoginAsync($"requester.{tenant.Slug}@opsflow.example");
        await SecurityTests.CreateWorkOrderAsync(requester, tenant.OrganizationId, "In range");
        var otherRequester = await fixture.LoginAsync($"requester.{other.Slug}@opsflow.example");
        await SecurityTests.CreateWorkOrderAsync(otherRequester, other.OrganizationId, "Other org");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var old = await db.WorkOrders.SingleAsync(w => w.OrganizationId == tenant.OrganizationId);
            old.CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }

        var admin = await fixture.LoginAsync($"admin.{tenant.Slug}@opsflow.example");
        var response = await admin.SendAsync(HttpMethod.Get, $"/api/organizations/{tenant.OrganizationId}/reports/summary?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, report.GetProperty("cohortCount").GetInt32());
    }

    private static async Task<HttpStatusCode> ClaimAsync(AuthedClient client, string url, Barrier barrier)
    {
        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        var response = await client.SendAsync(HttpMethod.Post, url, idempotencyKey: Guid.NewGuid().ToString("N"));
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> SendAsync(AuthedClient client, string url, object body, Barrier barrier)
    {
        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        var response = await client.SendAsync(HttpMethod.Post, url, body, idempotencyKey: "same-create-key-0001");
        return response.StatusCode;
    }
}

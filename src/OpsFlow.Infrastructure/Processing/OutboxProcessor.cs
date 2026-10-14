using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Options;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Processing;

public sealed class OutboxProcessor(
    IDbContextFactory<OpsFlowDbContext> dbFactory,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(settings.LeaseSeconds);
        var ids = await ClaimAsync(now, leaseUntil, settings.OutboxBatchSize, cancellationToken);
        foreach (var id in ids)
        {
            await ProcessOneAsync(id, settings.MaxAttempts, cancellationToken);
        }

        return ids.Count;
    }

    private async Task<List<Guid>> ClaimAsync(DateTime now, DateTime leaseUntil, int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @claimed TABLE (Id uniqueidentifier);
            ;WITH next_jobs AS (
                SELECT TOP (@batch) Id
                FROM dbo.OutboxMessages WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE NextAttemptAtUtc <= @now
                  AND (Status = N'Pending' OR (Status = N'Processing' AND LeaseUntilUtc IS NOT NULL AND LeaseUntilUtc < @now))
                ORDER BY NextAttemptAtUtc, Id
            )
            UPDATE m
            SET Status = N'Processing',
                LeaseUntilUtc = @leaseUntil,
                AttemptCount = AttemptCount + 1,
                UpdatedAtUtc = @now
            OUTPUT inserted.Id INTO @claimed
            FROM dbo.OutboxMessages m
            INNER JOIN next_jobs j ON j.Id = m.Id;
            SELECT Id FROM @claimed;
            """;
        AddParameter(command, "@batch", batchSize);
        AddParameter(command, "@now", now);
        AddParameter(command, "@leaseUntil", leaseUntil);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task ProcessOneAsync(Guid id, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (job is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<NotificationPayload>(job.Payload, JsonDefaults.Options)
                ?? throw new InvalidOperationException("Notification payload is empty.");
            var subject = $"OpsFlow {payload.Number}: {payload.Type}";
            var body = $"Work order {payload.Number} ({payload.Title}) has a new event: {payload.Type}.";
            foreach (var recipient in payload.Recipients.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await emailSender.SendAsync(recipient, subject, body, cancellationToken);
            }

            job.Status = OutboxStatus.Succeeded;
            job.LeaseUntilUtc = null;
            job.LastError = null;
            job.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Outbox job {JobId} of type {JobType} succeeded on attempt {Attempt}.", job.Id, job.Type, job.AttemptCount);
        }
        catch (Exception exception)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            job.LastError = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            job.LeaseUntilUtc = null;
            job.UpdatedAtUtc = now;
            if (job.AttemptCount >= maxAttempts)
            {
                job.Status = OutboxStatus.Failed;
                logger.LogWarning("Outbox job {JobId} of type {JobType} moved to the dead-letter state after {Attempt} attempts.", job.Id, job.Type, job.AttemptCount);
            }
            else
            {
                job.Status = OutboxStatus.Pending;
                var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
                job.NextAttemptAtUtc = now.Add(RetryBackoff.Delay(job.AttemptCount, jitter));
                logger.LogWarning("Outbox job {JobId} of type {JobType} failed on attempt {Attempt} and will retry.", job.Id, job.Type, job.AttemptCount);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record NotificationPayload(string Type, Guid WorkOrderId, string Number, string Title, List<string> Recipients);
}


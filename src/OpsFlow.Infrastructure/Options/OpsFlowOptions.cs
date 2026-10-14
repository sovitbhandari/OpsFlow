namespace OpsFlow.Infrastructure.Options;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";
    public int CompletedRetentionHours { get; set; } = 24;
    public int InProgressLeaseSeconds { get; set; } = 120;
}

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";
    public int PollIntervalSeconds { get; set; } = 5;
    public int OutboxBatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int SlaScanBatchSize { get; set; } = 50;
}

public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    public string Provider { get; set; } = "Local";
    public string LocalPath { get; set; } = "data/attachments";
    public string BlobContainerUri { get; set; } = "";
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxPerWorkOrder { get; set; } = 5;
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string FromAddress { get; set; } = "opsflow@localhost";
    public string FromName { get; set; } = "OpsFlow";
    public bool EnableSsl { get; set; }
}

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";
    public string KeysPath { get; set; } = "data/keys";
    public string BlobUri { get; set; } = "";
    public string ApplicationName { get; set; } = "OpsFlow";
}

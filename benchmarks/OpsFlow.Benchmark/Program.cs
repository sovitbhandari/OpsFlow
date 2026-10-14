using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("OPSFLOW_BENCHMARK_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("not measured");
    Console.WriteLine("Set OPSFLOW_BENCHMARK_CONNECTION to a disposable database whose name contains OpsFlowBenchmark.");
    return 2;
}

var builder = new SqlConnectionStringBuilder(connectionString);
if (!builder.InitialCatalog.Contains("OpsFlowBenchmark", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refusing to run. The database name must contain OpsFlowBenchmark.");
    return 2;
}

const string indexName = "IX_WorkOrders_Org_Status_Created_Number";
var options = new DbContextOptionsBuilder<OpsFlowDbContext>().UseSqlServer(builder.ConnectionString).Options;
await using (var db = new OpsFlowDbContext(options))
{
    await db.Database.MigrateAsync();
}

await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();
await SeedIfNeededAsync(connection);

var parameters = new BenchmarkParameters(OrganizationId: await FirstOrganizationAsync(connection), Status: "Open");
await DropIndexAsync(connection, indexName);
var before = await MeasureAsync(connection, parameters, "without-composite-index");
await CreateIndexAsync(connection, indexName);
var after = await MeasureAsync(connection, parameters, "with-composite-index");

var result = new
{
    status = "measured",
    environment = new
    {
        machine = Environment.MachineName,
        os = Environment.OSVersion.ToString(),
        processorCount = Environment.ProcessorCount,
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        database = builder.DataSource,
        catalog = builder.InitialCatalog,
        tier = "not queried; record the Azure SQL tier or local Docker SQL edition beside this file",
        sampleSize = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.WorkOrders")
    },
    query = "OrganizationId = @org AND Status = @status ORDER BY CreatedAtUtc DESC, Number DESC",
    parameters,
    notes = new[]
    {
        "Warm-up runs are excluded from the timing summary.",
        "Logical reads and duration still include plan-cache and client-network effects. The first measured run can be colder than the rest.",
        "This process drops and recreates only the named index, and only after the catalog name check."
    },
    before,
    after
};

Directory.CreateDirectory(Path.Combine("benchmarks", "results"));
var path = Path.Combine("benchmarks", "results", "latest.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {path}");
return 0;

static async Task SeedIfNeededAsync(SqlConnection connection)
{
    var count = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.WorkOrders");
    if (count >= 100_000)
    {
        return;
    }

    Console.WriteLine($"Seeding synthetic work orders. Current count is {count}.");
    var orgIds = new List<Guid>();
    for (var i = 0; i < 3; i++)
    {
        var id = Guid.NewGuid();
        orgIds.Add(id);
        await ExecuteAsync(connection, """
            INSERT INTO dbo.Organizations (Id, Name, Slug, NextWorkOrderNumber, CreatedAtUtc)
            VALUES (@id, @name, @slug, 200000, SYSUTCDATETIME());
            INSERT INTO dbo.AspNetUsers (Id, DisplayName, CreatedAtUtc, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, SecurityStamp, ConcurrencyStamp, LockoutEnabled, AccessFailedCount, PhoneNumberConfirmed, TwoFactorEnabled)
            VALUES (@userId, N'Benchmark', SYSUTCDATETIME(), @email, UPPER(@email), @email, UPPER(@email), 1, NEWID(), NEWID(), 0, 0, 0, 0);
            INSERT INTO dbo.OrganizationMemberships (Id, OrganizationId, UserId, Role, CreatedAtUtc)
            VALUES (NEWID(), @id, @userId, N'Requester', SYSUTCDATETIME());
            """,
            ("@id", id),
            ("@userId", Guid.NewGuid()),
            ("@name", $"Benchmark {i}"),
            ("@slug", $"bench-{id:N}"[..20]),
            ("@email", $"bench-{i}-{Guid.NewGuid():N}@opsflow.example"));
    }

    var table = new System.Data.DataTable();
    foreach (var column in new (string Name, Type Type)[]
    {
        ("Id", typeof(Guid)), ("OrganizationId", typeof(Guid)), ("Number", typeof(int)), ("Title", typeof(string)),
        ("Description", typeof(string)), ("Category", typeof(string)), ("Priority", typeof(string)), ("Status", typeof(string)),
        ("RequesterUserId", typeof(Guid)), ("CreatedAtUtc", typeof(DateTime)), ("UpdatedAtUtc", typeof(DateTime)),
        ("DueAtUtc", typeof(DateTime)), ("HoldAccumulatedSeconds", typeof(long)), ("SlaDurationSeconds", typeof(int))
    })
    {
        table.Columns.Add(column.Name, column.Type);
    }

    var statuses = new[] { "Open", "Assigned", "InProgress", "Resolved" };
    var priorities = new[] { "Low", "Medium", "High", "Critical" };
    var requesterByOrg = new Dictionary<Guid, Guid>();
    await using (var lookup = connection.CreateCommand())
    {
        lookup.CommandText = "SELECT OrganizationId, UserId FROM dbo.OrganizationMemberships WHERE OrganizationId IN (SELECT Id FROM dbo.Organizations WHERE Slug LIKE 'bench-%')";
        await using var reader = await lookup.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            requesterByOrg[reader.GetGuid(0)] = reader.GetGuid(1);
        }
    }

    var number = 1;
    for (var i = 0; i < 100_000; i++)
    {
        var org = orgIds[i % orgIds.Count];
        var created = DateTime.UtcNow.AddMinutes(-i);
        table.Rows.Add(Guid.NewGuid(), org, number++, $"Synthetic {i}", "Benchmark row", "HVAC", priorities[i % priorities.Length], statuses[i % statuses.Length], requesterByOrg[org], created, created, created.AddHours(4), 0L, 14400);
        if (table.Rows.Count == 5000)
        {
            await BulkAsync(connection, table);
            table.Clear();
        }
    }

    if (table.Rows.Count > 0)
    {
        await BulkAsync(connection, table);
    }
}

static async Task<Guid> FirstOrganizationAsync(SqlConnection connection)
{
    return await ScalarAsync<Guid>(connection, "SELECT TOP (1) Id FROM dbo.Organizations ORDER BY CreatedAtUtc");
}

static async Task DropIndexAsync(SqlConnection connection, string indexName)
{
    await ExecuteAsync(connection, $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'dbo.WorkOrders')) DROP INDEX {indexName} ON dbo.WorkOrders;");
}

static async Task CreateIndexAsync(SqlConnection connection, string indexName)
{
    await ExecuteAsync(connection, $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'dbo.WorkOrders'))
        CREATE INDEX {indexName} ON dbo.WorkOrders (OrganizationId, Status, CreatedAtUtc, Number)
        INCLUDE (Title, Priority, Category, AssignedTechnicianUserId, DueAtUtc, RequesterUserId);
        """);
}

static async Task<object> MeasureAsync(SqlConnection connection, BenchmarkParameters parameters, string label)
{
    const string sql = """
        SELECT Id, Number, Title, Priority, Category, Status, CreatedAtUtc, DueAtUtc
        FROM dbo.WorkOrders
        WHERE OrganizationId = @org AND Status = @status
        ORDER BY CreatedAtUtc DESC, Number DESC;
        """;

    for (var i = 0; i < 2; i++)
    {
        await RunQueryAsync(connection, sql, parameters, capturePlan: false);
    }

    var times = new List<double>();
    string? reads = null;
    string? plan = null;
    for (var i = 0; i < 5; i++)
    {
        var sample = await RunQueryAsync(connection, sql, parameters, capturePlan: i == 0);
        times.Add(sample.ElapsedMs);
        reads ??= sample.LogicalReads;
        plan ??= sample.PlanSnippet;
    }

    return new
    {
        label,
        warmupRuns = 2,
        measuredRuns = times.Count,
        elapsedMs = times,
        medianMs = Median(times),
        logicalReads = reads ?? "not captured",
        planSnippet = plan ?? "not captured"
    };
}

static async Task<QuerySample> RunQueryAsync(SqlConnection connection, string sql, BenchmarkParameters parameters, bool capturePlan)
{
    var messages = new List<string>();
    void Handler(object? sender, SqlInfoMessageEventArgs args) => messages.Add(args.Message);
    connection.InfoMessage += Handler;
    var clock = Stopwatch.StartNew();
    await using var command = connection.CreateCommand();
    command.CommandText = capturePlan ? "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON; " + sql : "SET STATISTICS IO ON; SET STATISTICS TIME ON; " + sql;
    Add(command, "@org", parameters.OrganizationId);
    Add(command, "@status", parameters.Status);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
    }

    string? plan = null;
    while (await reader.NextResultAsync())
    {
        if (capturePlan && reader.FieldCount > 0 && !reader.IsDBNull(0))
        {
            var text = reader.GetValue(0)?.ToString() ?? "";
            plan = text.Length > 500 ? text[..500] : text;
        }
    }

    clock.Stop();
    connection.InfoMessage -= Handler;
    var logical = messages.FirstOrDefault(message => message.Contains("logical reads", StringComparison.OrdinalIgnoreCase));
    return new QuerySample(clock.Elapsed.TotalMilliseconds, logical, plan);
}

static double Median(List<double> values)
{
    var ordered = values.OrderBy(value => value).ToArray();
    return ordered[ordered.Length / 2];
}

static async Task BulkAsync(SqlConnection connection, System.Data.DataTable table)
{
    using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.WorkOrders" };
    foreach (System.Data.DataColumn column in table.Columns)
    {
        bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
    }

    await bulk.WriteToServerAsync(table);
}

static async Task ExecuteAsync(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var parameter in parameters)
    {
        Add(command, parameter.Name, parameter.Value);
    }

    await command.ExecuteNonQueryAsync();
}

static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    var value = await command.ExecuteScalarAsync();
    if (typeof(T) == typeof(Guid))
    {
        return (T)(object)(value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));
    }

    return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
}

static void Add(SqlCommand command, string name, object value)
{
    command.Parameters.AddWithValue(name, value);
}

internal sealed record BenchmarkParameters(Guid OrganizationId, string Status);
internal sealed record QuerySample(double ElapsedMs, string? LogicalReads, string? PlanSnippet);

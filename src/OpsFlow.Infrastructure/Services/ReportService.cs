using System.Data;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

public sealed class ReportService(IDbContextFactory<OpsFlowDbContext> dbFactory) : IReportService
{
    public async Task<ReportSummaryDto> GetSummaryAsync(TenantActor actor, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        if (fromUtc.Kind == DateTimeKind.Local || toUtc.Kind == DateTimeKind.Local)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["from"] = ["Report dates must be UTC."] });
        }

        if (fromUtc >= toUtc)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["from"] = ["The start of the range must be earlier than the end."] });
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(366))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["to"] = ["The report range cannot exceed 366 days."] });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var membership = await TenantAccess.RequireMemberAsync(db, actor, cancellationToken);
        if (!PermissionMatrix.CanViewReports(membership.Role))
        {
            throw new ForbiddenException("You cannot view organization reports.");
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = ReportingSql.ProcedureName;
        command.CommandType = CommandType.StoredProcedure;
        var organization = command.CreateParameter();
        organization.ParameterName = "@OrganizationId";
        organization.Value = actor.OrganizationId;
        command.Parameters.Add(organization);
        var from = command.CreateParameter();
        from.ParameterName = "@FromUtc";
        from.Value = fromUtc;
        command.Parameters.Add(from);
        var to = command.CreateParameter();
        to.ParameterName = "@ToUtc";
        to.Value = toUtc;
        command.Parameters.Add(to);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var summary = new ReportSummaryDto(
            CohortCount: Convert.ToInt32(reader.GetInt64(0)),
            OverdueCount: reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
            ResolvedCount: reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
            AverageResolutionSeconds: reader.IsDBNull(3) ? null : reader.GetDouble(3),
            MinResolutionSeconds: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            MaxResolutionSeconds: reader.IsDBNull(5) ? null : reader.GetInt32(5),
            StatusCounts: [],
            PriorityCounts: []);

        var statuses = new List<CountByLabelDto>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(new CountByLabelDto(reader.GetString(0), Convert.ToInt32(reader.GetInt64(1))));
        }

        var priorities = new List<CountByLabelDto>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            priorities.Add(new CountByLabelDto(reader.GetString(0), Convert.ToInt32(reader.GetInt64(1))));
        }

        return summary with { StatusCounts = statuses, PriorityCounts = priorities };
    }
}

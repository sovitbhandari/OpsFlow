namespace OpsFlow.Infrastructure.Persistence;

public static class ReportingSql
{
    public const string ProcedureName = "dbo.usp_OrganizationWorkOrderReport";

    public const string CreateProcedure = """
        CREATE OR ALTER PROCEDURE dbo.usp_OrganizationWorkOrderReport
            @OrganizationId uniqueidentifier,
            @FromUtc datetime2,
            @ToUtc datetime2
        AS
        BEGIN
            SET NOCOUNT ON;

            IF @FromUtc >= @ToUtc
                THROW 50001, 'FromUtc must be earlier than ToUtc.', 1;

            -- Cohort: work orders created in [@FromUtc, @ToUtc) for one organization.
            -- Denominator (CohortCount) is that set.
            -- OverdueCount is the subset still Open, Assigned, or InProgress with DueAtUtc before now.
            -- OnHold is excluded because the SLA clock is paused.
            -- Resolution stats use wall-clock seconds from CreatedAtUtc to ResolvedAtUtc and only rows that have been resolved.
            -- The resolution denominator is ResolvedCount, not CohortCount.

            SELECT
                COUNT_BIG(*) AS CohortCount,
                CAST(SUM(CASE WHEN [Status] IN (N'Open', N'Assigned', N'InProgress') AND [DueAtUtc] < SYSUTCDATETIME() THEN 1 ELSE 0 END) AS bigint) AS OverdueCount,
                CAST(SUM(CASE WHEN [ResolvedAtUtc] IS NOT NULL THEN 1 ELSE 0 END) AS bigint) AS ResolvedCount,
                AVG(CASE WHEN [ResolvedAtUtc] IS NOT NULL THEN CAST(DATEDIFF(SECOND, [CreatedAtUtc], [ResolvedAtUtc]) AS float) END) AS AverageResolutionSeconds,
                MIN(CASE WHEN [ResolvedAtUtc] IS NOT NULL THEN DATEDIFF(SECOND, [CreatedAtUtc], [ResolvedAtUtc]) END) AS MinResolutionSeconds,
                MAX(CASE WHEN [ResolvedAtUtc] IS NOT NULL THEN DATEDIFF(SECOND, [CreatedAtUtc], [ResolvedAtUtc]) END) AS MaxResolutionSeconds
            FROM dbo.WorkOrders
            WHERE [OrganizationId] = @OrganizationId
              AND [CreatedAtUtc] >= @FromUtc
              AND [CreatedAtUtc] < @ToUtc;

            SELECT [Status] AS Label, CAST(COUNT_BIG(*) AS bigint) AS [Count]
            FROM dbo.WorkOrders
            WHERE [OrganizationId] = @OrganizationId
              AND [CreatedAtUtc] >= @FromUtc
              AND [CreatedAtUtc] < @ToUtc
            GROUP BY [Status];

            SELECT [Priority] AS Label, CAST(COUNT_BIG(*) AS bigint) AS [Count]
            FROM dbo.WorkOrders
            WHERE [OrganizationId] = @OrganizationId
              AND [CreatedAtUtc] >= @FromUtc
              AND [CreatedAtUtc] < @ToUtc
            GROUP BY [Priority];
        END
        """;

    public const string DropProcedure = "DROP PROCEDURE IF EXISTS dbo.usp_OrganizationWorkOrderReport;";
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpsFlow.Infrastructure.Persistence;

public sealed class OpsFlowDbContextFactory : IDesignTimeDbContextFactory<OpsFlowDbContext>
{
    public OpsFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpsFlowDbContext>();
        options.UseSqlServer("Server=localhost,1433;Database=OpsFlowDesignTime;User Id=sa;Password=design-time-only;Encrypt=True;TrustServerCertificate=True");
        return new OpsFlowDbContext(options.Options);
    }
}

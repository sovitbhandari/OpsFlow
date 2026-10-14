using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace OpsFlow.Infrastructure.Persistence;

public static class SqlErrors
{
    public static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }
}

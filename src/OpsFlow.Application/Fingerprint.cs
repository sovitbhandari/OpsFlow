using System.Security.Cryptography;
using System.Text;

namespace OpsFlow.Application;

public static class Fingerprint
{
    public static string Sha256(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    public static string ForCreate(CreateWorkOrderRequest request)
    {
        var canonical = string.Join('\n',
            request.Title.Trim(),
            request.Description.Trim(),
            request.Category.Trim(),
            request.Priority.ToString());
        return Sha256(canonical);
    }

    public static string ForClaim(Guid workOrderId) => Sha256(workOrderId.ToString("N"));
}

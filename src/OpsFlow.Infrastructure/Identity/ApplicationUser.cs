using Microsoft.AspNetCore.Identity;

namespace OpsFlow.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

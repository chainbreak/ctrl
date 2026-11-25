using Microsoft.AspNetCore.Identity;

namespace Ctrl.Api.Domain;

public sealed class AppUser : IdentityUser
{
    public string? Handle { get; set; }

    public string? NormalizedHandle { get; set; }

    public string Bio { get; set; } = string.Empty;

    public bool IsBanned { get; set; }

    public DateTimeOffset? BanExpiresAtUtc { get; set; }

    public string? BanReason { get; set; }

    public long ClicksBalance { get; set; }
}

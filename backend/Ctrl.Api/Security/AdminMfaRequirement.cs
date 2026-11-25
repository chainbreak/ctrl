using System.Security.Claims;
using Ctrl.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Ctrl.Api.Security;

public sealed class AdminMfaRequirement : IAuthorizationRequirement;

public sealed class AdminMfaHandler(UserManager<AppUser> users)
    : AuthorizationHandler<AdminMfaRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminMfaRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var user = await users.FindByIdAsync(userId);
        if (user is not null &&
            user.TwoFactorEnabled && context.User.HasClaim("amr","mfa") &&
            await users.IsInRoleAsync(user, Roles.Admin))
        {
            context.Succeed(requirement);
        }
    }
}

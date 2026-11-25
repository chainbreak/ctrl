using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ctrl.Api.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ctrl.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<AppUser> users,
    SignInManager<AppUser> signInManager,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    [ProducesResponseType<CsrfTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<CsrfTokenResponse> GetCsrfToken()
    {
        Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfTokenResponse(tokens.RequestToken ?? string.Empty));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return Unauthorized(new { message = "Неверный e-mail или пароль." });
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return StatusCode(StatusCodes.Status423Locked, new { message = "Вход временно заблокирован. Попробуйте позже." });
        }

        if (result.RequiresTwoFactor)
        {
            return Accepted(new LoginResponse(true, null));
        }

        if (!result.Succeeded)
        {
            return Unauthorized(new { message = "Неверный e-mail или пароль либо e-mail не подтверждён." });
        }

        // A password-only session can enroll an authenticator. Staff operations require an MFA claim.
        return Ok(new LoginResponse(false, new AuthenticatedUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.Handle,
            user.TwoFactorEnabled,
            await users.GetRolesAsync(user))));
    }

    [HttpPost("login/two-factor")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<LoginResponse>> LoginWithAuthenticator(
        TwoFactorLoginRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return Unauthorized(new { message = "Сначала выполните вход по паролю." });
        }

        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            request.Code,
            isPersistent: false,
            rememberClient: false);

        if (result.IsLockedOut)
        {
            return StatusCode(StatusCodes.Status423Locked, new { message = "Вход временно заблокирован. Попробуйте позже." });
        }

        if (!result.Succeeded)
        {
            return Unauthorized(new { message = "Неверный код подтверждения." });
        }

        return Ok(new LoginResponse(false, new AuthenticatedUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.Handle,
            user.TwoFactorEnabled,
            await users.GetRolesAsync(user))));
    }

    [HttpPost("login/recovery-code")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<LoginResponse>> LoginWithRecoveryCode(
        RecoveryCodeLoginRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return Unauthorized(new { message = "Сначала выполните вход по паролю." });
        }

        var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(request.Code);
        if (result.IsLockedOut)
        {
            return StatusCode(StatusCodes.Status423Locked, new { message = "Вход временно заблокирован. Попробуйте позже." });
        }

        if (!result.Succeeded)
        {
            return Unauthorized(new { message = "Недействительный или уже использованный код восстановления." });
        }

        return Ok(new LoginResponse(false, new AuthenticatedUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.Handle,
            user.TwoFactorEnabled,
            await users.GetRolesAsync(user))));
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<AuthenticatedUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticatedUserResponse>> GetCurrentUser(
        CancellationToken cancellationToken)
    {
        var user = await signInManager.UserManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(new AuthenticatedUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.Handle,
            user.TwoFactorEnabled,
            await users.GetRolesAsync(user)));
    }

    [HttpPost("two-factor/setup")]
    [Authorize]
    [ProducesResponseType<TwoFactorSetupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TwoFactorSetupResponse>> SetupTwoFactor(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await signInManager.UserManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return Conflict(new { message = "MFA уже включена для этого аккаунта." });
        }

        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Не удалось создать секрет MFA.");
        }

        var label = Uri.EscapeDataString($"CTRL:{user.Email}");
        var issuer = Uri.EscapeDataString("CTRL");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer={issuer}&digits=6";
        return Ok(new TwoFactorSetupResponse(key, uri));
    }

    [HttpPost("two-factor/enable")]
    [Authorize]
    [ProducesResponseType<EnableTwoFactorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EnableTwoFactorResponse>> EnableTwoFactor(
        EnableTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await signInManager.UserManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return Conflict(new { message = "MFA уже включена для этого аккаунта." });
        }

        var code = request.Code.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (!await users.VerifyTwoFactorTokenAsync(
                user,
                users.Options.Tokens.AuthenticatorTokenProvider,
                code))
        {
            return BadRequest(new { message = "Код приложения-аутентификатора не подошёл." });
        }

        var enabled = await users.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Не удалось включить MFA.");
        }

        var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        if (recoveryCodes is null)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Не удалось создать коды восстановления.");
        }

        await users.UpdateSecurityStampAsync(user);
        await signInManager.SignOutAsync();

        return Ok(new EnableTwoFactorResponse(recoveryCodes.ToArray()));
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }
}

/// <summary>Anti-forgery request token required for state-changing API requests.</summary>
public sealed record CsrfTokenResponse(string RequestToken);

/// <summary>Credentials used to begin an account sign-in.</summary>
public sealed record LoginRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public required string Email { get; init; }

    [Required, MinLength(12), MaxLength(128)]
    public required string Password { get; init; }
}

/// <summary>Result of password or second-factor authentication.</summary>
public sealed record LoginResponse(bool RequiresTwoFactor, AuthenticatedUserResponse? User);

/// <summary>Authenticated account data returned to the caller.</summary>
public sealed record AuthenticatedUserResponse(
    string Id,
    string Email,
    string? Handle,
    bool TwoFactorEnabled,
    IList<string> Roles);

/// <summary>Authenticator code used to complete sign-in.</summary>
public sealed record TwoFactorLoginRequest
{
    [Required, StringLength(8, MinimumLength = 6)]
    public required string Code { get; init; }
}

/// <summary>Recovery code used to complete sign-in when the authenticator is unavailable.</summary>
public sealed record RecoveryCodeLoginRequest
{
    [Required, StringLength(32, MinimumLength = 8)]
    public required string Code { get; init; }
}

/// <summary>Authenticator secret and enrollment URI. Treat both values as secrets.</summary>
public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

/// <summary>Authenticator code that confirms MFA enrollment.</summary>
public sealed record EnableTwoFactorRequest
{
    [Required, StringLength(8, MinimumLength = 6)]
    public required string Code { get; init; }
}

/// <summary>One-time recovery codes shown only when MFA is enabled.</summary>
public sealed record EnableTwoFactorResponse(IReadOnlyList<string> RecoveryCodes);

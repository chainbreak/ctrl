using System.Data;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ctrl.Api.Domain;
using Ctrl.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ctrl.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = Policies.AdminMfa)]
public sealed class AdminUsersController(
    CtrlDbContext database,
    UserManager<AppUser> users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> Search(
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return BadRequest(new { message = "Введите минимум два символа для поиска." });
        }

        var term = q.Trim();
        var normalizedTerm = term.ToUpperInvariant();
        var matches = await database.Users
            .AsNoTracking()
            .Where(user =>
                (user.Email != null && user.Email.ToUpper().Contains(normalizedTerm)) ||
                (user.NormalizedHandle != null && user.NormalizedHandle.Contains(normalizedTerm)))
            .OrderBy(user => user.Email)
            .Take(50)
            .Select(user => new AdminUserResponse(
                user.Id,
                user.Email ?? string.Empty,
                user.Handle,
                user.IsBanned,
                user.BanExpiresAtUtc,
                user.BanReason,
                user.ClicksBalance))
            .ToListAsync(cancellationToken);

        return Ok(matches);
    }

    [HttpPost("{userId}/ban")]
    public async Task<IActionResult> Ban(
        string userId,
        BanUserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
        {
            return BadRequest(new { message = "Укажите причину блокировки длиной до 500 символов." });
        }

        if (request.DurationHours is <= 0 or > 8760)
        {
            return BadRequest(new { message = "Срок блокировки должен быть от 1 часа до 365 дней." });
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Unauthorized();
        }

        if (string.Equals(actorId, userId, StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Нельзя заблокировать собственный аккаунт." });
        }

        var target = await users.FindByIdAsync(userId);
        if (target is null)
        {
            return NotFound();
        }

        if (await users.IsInRoleAsync(target, Roles.Admin))
        {
            return Conflict(new { message = "Администраторский аккаунт нельзя заблокировать этой операцией." });
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expiresAt = request.DurationHours is null
            ? null
            : now.AddHours(request.DurationHours.Value);
        var reason = request.Reason.Trim();

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        target.IsBanned = true;
        target.BanExpiresAtUtc = expiresAt;
        target.BanReason = reason;
        database.AdminAuditEntries.Add(new AdminAuditEntry
        {
            ActorUserId = actorId,
            TargetUserId = target.Id,
            Action = "user.ban",
            Reason = reason
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new { userId = target.Id, banned = true, expiresAt });
    }

    [HttpDelete("{userId}/ban")]
    public async Task<IActionResult> Unban(
        string userId,
        UnbanUserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
        {
            return BadRequest(new { message = "Укажите причину разблокировки длиной до 500 символов." });
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Unauthorized();
        }

        var target = await users.FindByIdAsync(userId);
        if (target is null)
        {
            return NotFound();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        target.IsBanned = false;
        target.BanExpiresAtUtc = null;
        target.BanReason = null;
        database.AdminAuditEntries.Add(new AdminAuditEntry
        {
            ActorUserId = actorId,
            TargetUserId = target.Id,
            Action = "user.unban",
            Reason = request.Reason.Trim()
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new { userId = target.Id, banned = false });
    }

    [HttpPost("{userId}/clicks")]
    public async Task<IActionResult> AdjustClicks(
        string userId,
        AdjustClicksRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AmountDelta is 0 or < -1_000_000_000 or > 1_000_000_000)
        {
            return BadRequest(new { message = "Изменение должно быть ненулевым и не превышать 1 000 000 000 кликов." });
        }

        if (request.OperationId == Guid.Empty)
        {
            return BadRequest(new { message = "Укажите уникальный идентификатор операции." });
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
        {
            return BadRequest(new { message = "Укажите причину изменения баланса длиной до 500 символов." });
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Unauthorized();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var previousOperation = await database.ClickLedgerEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.OperationId == request.OperationId, cancellationToken);
        if (previousOperation is not null)
        {
            if (previousOperation.UserId == userId &&
                previousOperation.ActorUserId == actorId &&
                previousOperation.AmountDelta == request.AmountDelta &&
                previousOperation.Reason == request.Reason.Trim())
            {
                return Ok(new
                {
                    userId,
                    balance = previousOperation.BalanceAfter,
                    operationId = request.OperationId,
                    replayed = true
                });
            }

            return Conflict(new { message = "Идентификатор операции уже использован для другого изменения." });
        }

        var target = await users.FindByIdAsync(userId);
        if (target is null)
        {
            return NotFound();
        }

        long balanceAfter;
        try
        {
            balanceAfter = checked(target.ClicksBalance + request.AmountDelta);
        }
        catch (OverflowException)
        {
            return BadRequest(new { message = "Баланс кликов вышел за допустимый диапазон." });
        }

        if (balanceAfter < 0)
        {
            return Conflict(new { message = "Нельзя списать больше кликов, чем есть на балансе." });
        }

        target.ClicksBalance = balanceAfter;
        database.ClickLedgerEntries.Add(new ClickLedgerEntry
        {
            UserId = target.Id,
            ActorUserId = actorId,
            AmountDelta = request.AmountDelta,
            BalanceAfter = balanceAfter,
            Reason = request.Reason.Trim(),
            OperationId = request.OperationId
        });
        database.AdminAuditEntries.Add(new AdminAuditEntry
        {
            ActorUserId = actorId,
            TargetUserId = target.Id,
            Action = "user.clicks.adjust",
            Reason = request.Reason.Trim()
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            userId = target.Id,
            balance = balanceAfter,
            operationId = request.OperationId,
            replayed = false
        });
    }
}

/// <summary>Account details exposed to authorized administrators.</summary>
public sealed record AdminUserResponse(
    string Id,
    string Email,
    string? Handle,
    bool IsBanned,
    DateTimeOffset? BanExpiresAtUtc,
    string? BanReason,
    long ClicksBalance);

/// <summary>Reason and optional duration for an account ban.</summary>
public sealed record BanUserRequest
{
    [Required, StringLength(500, MinimumLength = 1)]
    public required string Reason { get; init; }

    [Range(1, 8760)]
    public int? DurationHours { get; init; }
}

/// <summary>Audited adjustment to an account's Clicks balance.</summary>
public sealed record UnbanUserRequest
{
    [Required, StringLength(500, MinimumLength = 1)]
    public required string Reason { get; init; }
}

/// <summary>Audited, idempotent adjustment to an account's Clicks balance.</summary>
public sealed record AdjustClicksRequest
{
    [Range(-1_000_000_000, 1_000_000_000)]
    public required long AmountDelta { get; init; }

    [Required, StringLength(500, MinimumLength = 1)]
    public required string Reason { get; init; }

    [Required]
    public required Guid OperationId { get; init; }
}

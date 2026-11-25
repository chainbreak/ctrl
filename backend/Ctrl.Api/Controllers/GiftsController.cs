using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ctrl.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ctrl.Api.Controllers;

[ApiController]
[Route("api/gifts")]
[Authorize]
public sealed class GiftsController(IGiftService gifts) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<GiftCatalogResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GiftCatalogResponse>> GetCatalog(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var catalog = await gifts.GetCatalogAsync(userId, cancellationToken);
        if (catalog is null)
        {
            return NotFound();
        }

        return Ok(new GiftCatalogResponse(
            catalog.ClicksBalance,
            catalog.Items.Select(item => new GiftCatalogItemResponse(
                item.Id,
                item.Name,
                item.Description,
                item.Price,
                item.Owned)).ToArray()));
    }

    [HttpPost("purchase")]
    [ProducesResponseType<GiftPurchaseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<GiftPurchaseResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GiftPurchaseResponse>> Purchase(
        PurchaseGiftRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
        {
            return BadRequest(new { message = "Укажите уникальный идентификатор операции." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var result = await gifts.PurchaseAsync(
            userId,
            request.GiftId,
            request.OperationId,
            cancellationToken);

        switch (result.Outcome)
        {
            case GiftPurchaseOutcome.Purchased:
                var response = new GiftPurchaseResponse(
                request.GiftId,
                result.ClicksBalance,
                result.Owned,
                result.Replayed);
                return result.Replayed ? Ok(response) : Created("/api/gifts", response);
            case GiftPurchaseOutcome.GiftNotFound:
            case GiftPurchaseOutcome.AccountNotFound:
                return NotFound();
            case GiftPurchaseOutcome.InsufficientBalance:
                return Conflict(new { message = "Недостаточно кликов для покупки подарка." });
            case GiftPurchaseOutcome.OperationIdConflict:
                return Conflict(new { message = "Идентификатор операции уже использован." });
            default:
                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Не удалось выполнить покупку подарка.");
        }
    }
}

/// <summary>Gift catalog and current Clicks balance for an authenticated account.</summary>
public sealed record GiftCatalogResponse(
    long ClicksBalance,
    IReadOnlyList<GiftCatalogItemResponse> Items);

/// <summary>A gift available for purchase and the number owned by the account.</summary>
public sealed record GiftCatalogItemResponse(
    string Id,
    string Name,
    string Description,
    long Price,
    int Owned);

/// <summary>Idempotent request to purchase a gift with Clicks.</summary>
public sealed record PurchaseGiftRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public required string GiftId { get; init; }

    [Required]
    public required Guid OperationId { get; init; }
}

/// <summary>Result of a completed or replayed gift purchase.</summary>
public sealed record GiftPurchaseResponse(
    string GiftId,
    long ClicksBalance,
    int Owned,
    bool Replayed);

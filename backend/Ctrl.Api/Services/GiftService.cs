using System.Data;
using Ctrl.Api.Domain;
using Ctrl.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ctrl.Api.Services;

public sealed class GiftService(CtrlDbContext database) : IGiftService
{
    private static readonly GiftDefinition[] GiftCatalog =
    [
        new("first-gift", "Подарок CTRL", "Анимированный подарок CTRL.", 50),
        new("webm-gift-1", "Подарок 2", "Анимированный подарок CTRL.", 50),
        new("webm-gift-2", "Подарок 3", "Анимированный подарок CTRL.", 50)
    ];

    public async Task<GiftCatalogResult?> GetCatalogAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(account => account.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var ownedCounts = await database.GiftInventoryEntries
            .Where(entry => entry.UserId == userId)
            .GroupBy(entry => entry.GiftId)
            .Select(group => new { GiftId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.GiftId, entry => entry.Count, cancellationToken);

        return new GiftCatalogResult(
            user.ClicksBalance,
            GiftCatalog
                .Select(gift => new GiftCatalogItem(
                    gift.Id,
                    gift.Name,
                    gift.Description,
                    gift.Price,
                    ownedCounts.GetValueOrDefault(gift.Id)))
                .ToArray());
    }

    public async Task<GiftPurchaseResult> PurchaseAsync(
        string userId,
        string giftId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var gift = GiftCatalog.SingleOrDefault(
            item => string.Equals(item.Id, giftId, StringComparison.Ordinal));
        if (gift is null)
        {
            return new GiftPurchaseResult(GiftPurchaseOutcome.GiftNotFound);
        }

        if (operationId == Guid.Empty)
        {
            return new GiftPurchaseResult(GiftPurchaseOutcome.OperationIdConflict);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var previousPurchase = await database.GiftInventoryEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.OperationId == operationId, cancellationToken);

        var user = await database.Users
            .SingleOrDefaultAsync(account => account.Id == userId, cancellationToken);
        if (user is null)
        {
            return new GiftPurchaseResult(GiftPurchaseOutcome.AccountNotFound);
        }

        if (previousPurchase is not null)
        {
            if (previousPurchase.UserId != userId || previousPurchase.GiftId != giftId)
            {
                return new GiftPurchaseResult(GiftPurchaseOutcome.OperationIdConflict);
            }

            var replayedOwned = await database.GiftInventoryEntries
                .CountAsync(entry => entry.UserId == userId && entry.GiftId == giftId, cancellationToken);
            return new GiftPurchaseResult(
                GiftPurchaseOutcome.Purchased,
                user.ClicksBalance,
                replayedOwned,
                Replayed: true);
        }

        var operationAlreadyUsed = await database.ClickLedgerEntries
            .AnyAsync(entry => entry.OperationId == operationId, cancellationToken);
        if (operationAlreadyUsed)
        {
            return new GiftPurchaseResult(GiftPurchaseOutcome.OperationIdConflict);
        }

        if (user.ClicksBalance < gift.Price)
        {
            return new GiftPurchaseResult(
                GiftPurchaseOutcome.InsufficientBalance,
                user.ClicksBalance);
        }

        user.ClicksBalance -= gift.Price;
        database.GiftInventoryEntries.Add(new GiftInventoryEntry
        {
            UserId = userId,
            GiftId = giftId,
            OperationId = operationId
        });
        database.ClickLedgerEntries.Add(new ClickLedgerEntry
        {
            UserId = userId,
            ActorUserId = userId,
            AmountDelta = -gift.Price,
            BalanceAfter = user.ClicksBalance,
            Reason = $"gift.purchase:{giftId}",
            OperationId = operationId
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var owned = await database.GiftInventoryEntries
            .CountAsync(entry => entry.UserId == userId && entry.GiftId == giftId, cancellationToken);

        return new GiftPurchaseResult(
            GiftPurchaseOutcome.Purchased,
            user.ClicksBalance,
            owned);
    }

    private sealed record GiftDefinition(string Id, string Name, string Description, long Price);
}

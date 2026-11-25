namespace Ctrl.Api.Services;

public interface IGiftService
{
    Task<GiftCatalogResult?> GetCatalogAsync(string userId, CancellationToken cancellationToken);

    Task<GiftPurchaseResult> PurchaseAsync(
        string userId,
        string giftId,
        Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record GiftCatalogItem(
    string Id,
    string Name,
    string Description,
    long Price,
    int Owned);

public sealed record GiftCatalogResult(
    long ClicksBalance,
    IReadOnlyList<GiftCatalogItem> Items);

public enum GiftPurchaseOutcome
{
    Purchased,
    GiftNotFound,
    InsufficientBalance,
    OperationIdConflict,
    AccountNotFound
}

public sealed record GiftPurchaseResult(
    GiftPurchaseOutcome Outcome,
    long ClicksBalance = 0,
    int Owned = 0,
    bool Replayed = false);

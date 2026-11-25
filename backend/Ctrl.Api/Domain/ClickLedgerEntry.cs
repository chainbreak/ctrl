namespace Ctrl.Api.Domain;

public sealed class ClickLedgerEntry
{
    public long Id { get; set; }

    public required string UserId { get; set; }

    public required string ActorUserId { get; set; }

    public long AmountDelta { get; set; }

    public long BalanceAfter { get; set; }

    public required string Reason { get; set; }

    public Guid OperationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

namespace Ctrl.Api.Domain;

public sealed class GiftInventoryEntry
{
    public long Id { get; set; }

    public required string UserId { get; set; }

    public required string GiftId { get; set; }

    public Guid OperationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

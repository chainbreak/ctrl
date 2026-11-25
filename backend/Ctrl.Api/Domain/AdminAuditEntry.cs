namespace Ctrl.Api.Domain;

public sealed class AdminAuditEntry
{
    public long Id { get; set; }

    public required string ActorUserId { get; set; }

    public required string TargetUserId { get; set; }

    public required string Action { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

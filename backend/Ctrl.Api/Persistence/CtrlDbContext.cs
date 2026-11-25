using Ctrl.Api.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ctrl.Api.Persistence;

public sealed class CtrlDbContext(DbContextOptions<CtrlDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<AdminAuditEntry> AdminAuditEntries => Set<AdminAuditEntry>();

    public DbSet<ClickLedgerEntry> ClickLedgerEntries => Set<ClickLedgerEntry>();

    public DbSet<GiftInventoryEntry> GiftInventoryEntries => Set<GiftInventoryEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>()
            .HasIndex(user => user.NormalizedHandle)
            .IsUnique();

        builder.Entity<AdminAuditEntry>()
            .HasIndex(entry => entry.CreatedAtUtc);

        builder.Entity<ClickLedgerEntry>()
            .HasIndex(entry => entry.OperationId)
            .IsUnique();

        builder.Entity<ClickLedgerEntry>()
            .HasIndex(entry => new { entry.UserId, entry.CreatedAtUtc });

        builder.Entity<GiftInventoryEntry>()
            .HasIndex(entry => entry.OperationId)
            .IsUnique();

        builder.Entity<GiftInventoryEntry>()
            .HasIndex(entry => new { entry.UserId, entry.GiftId, entry.CreatedAtUtc });

        builder.Entity<GiftInventoryEntry>()
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

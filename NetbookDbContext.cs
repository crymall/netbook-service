using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

public class NetbookDbContext(DbContextOptions<NetbookDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Each IAM account maps to at most one local user; this also backstops
        // the sync endpoints against duplicate pushes from iam-service.
        modelBuilder.Entity<User>().HasIndex(u => u.IamId).IsUnique();

        // iam-service enforces unique usernames, so a collision here means the
        // mirror is stale (e.g. a missed deletion sync) — surface it rather
        // than silently accumulating duplicates.
        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();

        // Real FK with cascade: deleting a user deletes their notes at the
        // database level, and an orphaned note can't exist. No navigation
        // properties — controllers only ever filter by the key.
        modelBuilder.Entity<Note>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

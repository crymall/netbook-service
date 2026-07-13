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
    }
}

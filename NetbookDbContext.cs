using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

public class NetbookDbContext(DbContextOptions<NetbookDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var user1IamId = Guid.NewGuid();
        var user2IamId = Guid.NewGuid();

        // Seed Users
        modelBuilder
            .Entity<User>()
            .HasData(
                new User
                {
                    Id = 1,
                    IamId = user1IamId,
                    Username = "seed_user_1",
                },
                new User
                {
                    Id = 2,
                    IamId = user2IamId,
                    Username = "seed_user_2",
                }
            );

        // Seed Notes
        modelBuilder
            .Entity<Note>()
            .HasData(
                new Note
                {
                    Id = Guid.NewGuid(),
                    Title = "Database Seed Note 1",
                    Content = "This note came from the database seed.",
                    UserId = "1",
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new Note
                {
                    Id = Guid.NewGuid(),
                    Title = "Database Seed Note 2",
                    Content = "Another seeded note.",
                    UserId = "2",
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                }
            );
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace netbook_service.Tests;

public static class TestConstants
{
    // 32+ bytes: .NET's HS256 validator silently rejects shorter keys.
    public const string JwtSecret = "test-secret-0123456789abcdefghijklmnopqrstuvwxyz";
    public const string ApiKey = "test_api_key";
}

// Boots the real app pipeline (routing, JWT auth, [ApiKey] filter) against a
// SQLite in-memory database. The kept-open connection is what keeps the
// in-memory database alive for the factory's lifetime.
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting lands in host configuration, which Program.cs's inline
        // Configuration reads can see; ConfigureAppConfiguration would apply
        // too late for top-level statements.
        builder.UseSetting("Jwt:Secret", TestConstants.JwtSecret);
        builder.UseSetting("MiddenApiKey", TestConstants.ApiKey);

        builder.ConfigureServices(services =>
        {
            // Strip the Npgsql registration (EF Core 9+ keeps provider config in
            // IDbContextOptionsConfiguration, so removing only the options is
            // not enough) and swap in SQLite.
            services.RemoveAll(typeof(DbContextOptions<NetbookDbContext>));
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<NetbookDbContext>));

            _connection.Open();
            services.AddDbContext<NetbookDbContext>(options => options.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<NetbookDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

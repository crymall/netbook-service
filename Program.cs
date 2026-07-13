using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddControllers();

// Add JWT Authentication
var jwtSecret =
    builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException(
        "Jwt:Secret is not configured. Set it (32+ bytes) to the same value as iam-service's JWT_SECRET."
    );

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.ContainsKey("token"))
                {
                    context.Token = context.Request.Cookies["token"];
                }
                return Task.CompletedTask;
            }
        };
    });

// Outside Development, an unset MiddenApiKey must stop the pod at startup,
// not fall back to the dev key (the ApiKey filter also fails closed).
if (
    !builder.Environment.IsDevelopment()
    && !builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrEmpty(builder.Configuration["MiddenApiKey"])
)
{
    throw new InvalidOperationException(
        "MiddenApiKey is not configured. Set it to the same value as iam-service's MIDDEN_API_KEY."
    );
}

// Configure the database. In the cluster the DB_* env vars come from
// netbook-secrets (same convention as iam-service/canteen-service); locally
// the ConnectionStrings:Netbook fallback points at a dev Postgres. The
// builder handles quoting, so generated passwords can contain any character.
var dbHost = builder.Configuration["DB_HOST"];
var connectionString = !string.IsNullOrEmpty(dbHost)
    ? new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = dbHost,
        Port = int.TryParse(builder.Configuration["DB_PORT"], out var dbPort) ? dbPort : 5432,
        Username = builder.Configuration["DB_USER"],
        Password = builder.Configuration["DB_PASSWORD"],
        Database = builder.Configuration["DB_NAME"],
    }.ConnectionString
    : builder.Configuration.GetConnectionString("Netbook")
        ?? "Host=localhost;Port=5432;Username=netbook;Password=netbook;Database=netbook_db";
builder.Services.AddDbContext<NetbookDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks().AddDbContextCheck<NetbookDbContext>();

var app = builder.Build();

// Apply pending migrations on startup — the deployment's equivalent of the
// Node services' "npm run db:init && npm start". The test host swaps in its
// own database, so it skips this.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NetbookDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No UseHttpsRedirection: TLS terminates at the nginx ingress, which forwards
// plain HTTP to the pod — an in-app redirect would loop.

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpMetrics();

app.MapControllers();
app.MapMetrics();
app.MapHealthChecks("/healthz");

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in the test project.
public partial class Program { }

using Microsoft.EntityFrameworkCore;

// using netbook_service;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddControllers();

// Configure the database
var connectionString =
    builder.Configuration.GetConnectionString("Netbook") ?? "Data Source=.db/Netbook.db";
builder.Services.AddSqlite<NetbookDbContext>(connectionString);

var app = builder.Build();

// Ensure the database is created and seeded on startup - DELETE AFTER PROTOTYPING
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NetbookDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

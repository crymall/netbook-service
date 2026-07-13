using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using netbook_service.Models;

namespace netbook_service.Tests;

public static class TestHelpers
{
    // Mints a token with the same claim shape iam-service signs: an "id"
    // claim holding the IAM user's UUID and a "username" claim.
    public static string MintToken(Guid iamId, string username, string? secret = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? TestConstants.JwtSecret));
        var token = new JwtSecurityToken(
            claims:
            [
                new Claim("id", iamId.ToString()),
                new Claim("username", username),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpClient WithBearer(this HttpClient client, Guid iamId, string username)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(iamId, username));
        return client;
    }

    public static HttpClient WithApiKey(this HttpClient client)
    {
        client.DefaultRequestHeaders.Add("x-api-key", TestConstants.ApiKey);
        return client;
    }

    // Provisions a user the way iam-service does: a POST /users push
    // authenticated by api key alone.
    public static async Task<User> SyncUserAsync(
        TestWebApplicationFactory factory, Guid iamId, string username)
    {
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.PostAsJsonAsync(
            "/users", new { iam_id = iamId, username });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<User>())!;
    }
}

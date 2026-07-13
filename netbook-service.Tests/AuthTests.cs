using System.Net;
using netbook_service.Models;

namespace netbook_service.Tests;

public class AuthTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Notes_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Notes_WithGarbageToken_Returns401()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var response = await client.GetAsync("/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Notes_TokenSignedWithWrongSecret_Returns401()
    {
        using var client = factory.CreateClient();
        var token = TestHelpers.MintToken(
            Guid.NewGuid(), "intruder", "wrong-secret-0123456789abcdefghijklmnopqrstuvwxyz");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync("/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Notes_TokenFromCookie_IsAccepted()
    {
        // iam-service sets an httpOnly "token" cookie on login; the bearer
        // handler is configured to read it.
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), "cookie_user");
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/notes");
        request.Headers.Add("Cookie", $"token={TestHelpers.MintToken(user.IamId, user.Username)}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Notes_ValidTokenButNoLocalUser_Returns403()
    {
        // JIT provisioning is gone: an IAM account that was never synced here
        // is authenticated but not authorized.
        using var client = factory.CreateClient().WithBearer(Guid.NewGuid(), "unsynced_user");
        var response = await client.GetAsync("/notes");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_IsAnonymousAndHealthy()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_IsExposed()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("http_request", await response.Content.ReadAsStringAsync());
    }
}

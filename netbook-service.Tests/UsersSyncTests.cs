using System.Net;
using System.Net.Http.Json;
using netbook_service.Models;

namespace netbook_service.Tests;

public class UsersSyncTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task PostUser_WithoutApiKey_IsRejected()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/users", new { iam_id = Guid.NewGuid(), username = "nokey" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUser_WithoutApiKey_IsRejected()
    {
        // The whole controller is a machine surface — even reads need the key.
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), "hidden");
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/users/{user.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UsersList_IsGone()
    {
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.GetAsync("/users");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task PostUser_BindsSnakeCaseIamId()
    {
        // iam-service sends { username, iam_id } — the exact wire shape.
        var iamId = Guid.NewGuid();
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.PostAsJsonAsync(
            "/users", new { username = "wire_shape", iam_id = iamId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<User>())!;
        Assert.Equal(iamId, user.IamId);
        Assert.Equal("wire_shape", user.Username);
        Assert.True(user.Id > 0);
    }

    [Fact]
    public async Task PostUser_DuplicateIamId_IsIdempotent()
    {
        var iamId = Guid.NewGuid();
        using var client = factory.CreateClient().WithApiKey();

        var first = await client.PostAsJsonAsync(
            "/users", new { username = "dupe", iam_id = iamId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<User>())!;

        var second = await client.PostAsJsonAsync(
            "/users", new { username = "dupe", iam_id = iamId });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var existing = (await second.Content.ReadFromJsonAsync<User>())!;
        Assert.Equal(created.Id, existing.Id);
    }

    [Fact]
    public async Task PostUser_TakenUsernameDifferentIamId_IsConflict()
    {
        using var client = factory.CreateClient().WithApiKey();

        var first = await client.PostAsJsonAsync(
            "/users", new { username = "highlander", iam_id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/users", new { username = "highlander", iam_id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PostUser_EmptyIamId_IsBadRequest()
    {
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.PostAsJsonAsync(
            "/users", new { username = "noguid", iam_id = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostUser_OverlongUsername_IsBadRequest()
    {
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.PostAsJsonAsync(
            "/users", new { username = new string('u', 51), iam_id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUserSync_RemovesUserAndCascadesNotes()
    {
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), "delete_me");
        using var authed = factory.CreateClient().WithBearer(user.IamId, user.Username);
        await authed.PostAsJsonAsync("/notes", new { title = "orphan?", content = "o" });

        using var syncClient = factory.CreateClient().WithApiKey();
        var response = await syncClient.DeleteAsync($"/users/sync/{user.IamId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The account is gone (403 now) and so are its notes: re-syncing the
        // same IAM account yields a fresh empty notebook, not the old notes.
        var after = await authed.GetAsync("/notes");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);

        var resynced = await TestHelpers.SyncUserAsync(factory, user.IamId, user.Username);
        using var reAuthed = factory.CreateClient().WithBearer(resynced.IamId, resynced.Username);
        var notes = (await reAuthed.GetFromJsonAsync<PagedNotes>("/notes"))!;
        Assert.Empty(notes.Items);
    }

    [Fact]
    public async Task DeleteUserSync_UnknownIamId_Returns404()
    {
        using var client = factory.CreateClient().WithApiKey();
        var response = await client.DeleteAsync($"/users/sync/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUserSync_WithoutApiKey_IsRejected()
    {
        using var client = factory.CreateClient();
        var response = await client.DeleteAsync($"/users/sync/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutAndPlainDelete_AreGone()
    {
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), "immutable");
        using var client = factory.CreateClient().WithApiKey();

        var put = await client.PutAsJsonAsync(
            $"/users/{user.Id}", new { id = user.Id, username = "hacked" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);

        // Deletion goes through the iam-id sync route only.
        var del = await client.DeleteAsync($"/users/{user.Id}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, del.StatusCode);
    }
}

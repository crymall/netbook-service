using System.Net;
using System.Net.Http.Json;
using netbook_service.Models;

namespace netbook_service.Tests;

public class NotesTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<(HttpClient Client, User User)> AuthedClientAsync(string username)
    {
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), username);
        var client = factory.CreateClient().WithBearer(user.IamId, user.Username);
        return (client, user);
    }

    [Fact]
    public async Task PostNote_StampsServerOwnedFields()
    {
        var (client, user) = await AuthedClientAsync("stamp_user");
        using var _ = client;

        // Extra JSON keys for server-owned fields are ignored — the write DTO
        // doesn't bind them at all.
        var forgedId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/notes", new
        {
            id = forgedId,
            title = "Groceries",
            content = "Eggs, flour",
            userId = 999,
            createdAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<Note>())!;
        Assert.NotEqual(forgedId, note.Id);
        Assert.Equal(user.Id, note.UserId);
        Assert.True(note.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal("Groceries", note.Title);
    }

    [Fact]
    public async Task PostNote_OverlongTitle_IsBadRequest()
    {
        var (client, _) = await AuthedClientAsync("verbose_user");
        using var _c = client;
        var response = await client.PostAsJsonAsync("/notes", new
        {
            title = new string('x', 201),
            content = "c",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetNotes_ReturnsOnlyCallersNotes()
    {
        var (alice, _) = await AuthedClientAsync("alice_list");
        var (bob, _) = await AuthedClientAsync("bob_list");
        using (alice)
        using (bob)
        {
            await alice.PostAsJsonAsync("/notes", new { title = "alice note", content = "a" });
            await bob.PostAsJsonAsync("/notes", new { title = "bob note", content = "b" });

            var aliceNotes = (await alice.GetFromJsonAsync<List<Note>>("/notes"))!;
            Assert.All(aliceNotes, n => Assert.Equal("alice note", n.Title));
            Assert.Single(aliceNotes);
        }
    }

    [Fact]
    public async Task GetNote_ForeignNote_Returns404NotForbidden()
    {
        var (owner, _) = await AuthedClientAsync("owner_get");
        var (other, _) = await AuthedClientAsync("other_get");
        using (owner)
        using (other)
        {
            var created = await owner.PostAsJsonAsync("/notes", new { title = "secret", content = "s" });
            var note = (await created.Content.ReadFromJsonAsync<Note>())!;

            var response = await other.GetAsync($"/notes/{note.Id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var ownerResponse = await owner.GetAsync($"/notes/{note.Id}");
            Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        }
    }

    [Fact]
    public async Task PutNote_UpdatesOwnNote_And404sOnForeign()
    {
        var (owner, _) = await AuthedClientAsync("owner_put");
        var (other, _) = await AuthedClientAsync("other_put");
        using (owner)
        using (other)
        {
            var created = await owner.PostAsJsonAsync("/notes", new { title = "before", content = "c" });
            var note = (await created.Content.ReadFromJsonAsync<Note>())!;

            var update = new { id = note.Id, title = "after", content = "c2" };

            var foreign = await other.PutAsJsonAsync($"/notes/{note.Id}", update);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

            var own = await owner.PutAsJsonAsync($"/notes/{note.Id}", update);
            Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);

            var fetched = (await owner.GetFromJsonAsync<Note>($"/notes/{note.Id}"))!;
            Assert.Equal("after", fetched.Title);
        }
    }

    [Fact]
    public async Task PutNote_MismatchedIds_ReturnsBadRequest()
    {
        var (client, _) = await AuthedClientAsync("mismatch_put");
        using var _c = client;
        var response = await client.PutAsJsonAsync(
            $"/notes/{Guid.NewGuid()}", new { id = Guid.NewGuid(), title = "x", content = "y" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteNote_RemovesOwnNote_And404sOnForeign()
    {
        var (owner, _) = await AuthedClientAsync("owner_del");
        var (other, _) = await AuthedClientAsync("other_del");
        using (owner)
        using (other)
        {
            var created = await owner.PostAsJsonAsync("/notes", new { title = "doomed", content = "d" });
            var note = (await created.Content.ReadFromJsonAsync<Note>())!;

            var foreign = await other.DeleteAsync($"/notes/{note.Id}");
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

            var own = await owner.DeleteAsync($"/notes/{note.Id}");
            Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);

            var gone = await owner.GetAsync($"/notes/{note.Id}");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task NotesUserRoute_IsGone()
    {
        var (client, user) = await AuthedClientAsync("route_check");
        using var _c = client;
        var response = await client.GetAsync($"/notes/user/{user.Id}");
        // The old per-user listing was redundant with GET /notes and is
        // removed; /notes/user/{id} no longer parses as a note id either.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

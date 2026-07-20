using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using netbook_service.Models;

namespace netbook_service.Tests;

// Mirrors the controller's PagedResult<Note> for deserialization.
public record PagedNotes(List<Note> Items, int Page, int PageSize, int Total, int TotalPages);

public class NotesTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<(HttpClient Client, User User)> AuthedClientAsync(string username)
    {
        var user = await TestHelpers.SyncUserAsync(factory, Guid.NewGuid(), username);
        var client = factory.CreateClient().WithBearer(user.IamId, user.Username);
        return (client, user);
    }

    private static async Task CreateNotesAsync(HttpClient client, int count, string prefix)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/notes", new { title = $"{prefix} {i:D2}", content = $"body {i}" });
            response.EnsureSuccessStatusCode();
        }
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
            updatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<Note>())!;
        Assert.NotEqual(forgedId, note.Id);
        Assert.Equal(user.Id, note.UserId);
        Assert.True(note.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(note.CreatedAt, note.UpdatedAt);
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

            var aliceNotes = (await alice.GetFromJsonAsync<PagedNotes>("/notes"))!;
            Assert.Equal(1, aliceNotes.Total);
            Assert.All(aliceNotes.Items, n => Assert.Equal("alice note", n.Title));
        }
    }

    [Fact]
    public async Task GetNotes_PaginatesAndReportsTotals()
    {
        var (client, _) = await AuthedClientAsync("paginator");
        using var _c = client;
        await CreateNotesAsync(client, 12, "note");

        var page1 = (await client.GetFromJsonAsync<PagedNotes>("/notes?page=1&pageSize=10"))!;
        Assert.Equal(12, page1.Total);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(1, page1.Page);
        Assert.Equal(10, page1.Items.Count);

        var page2 = (await client.GetFromJsonAsync<PagedNotes>("/notes?page=2&pageSize=10"))!;
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal(2, page2.Page);

        // No note appears on both pages.
        var overlap = page1.Items.Select(n => n.Id).Intersect(page2.Items.Select(n => n.Id));
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task GetNotes_OrdersMostRecentlyUpdatedFirst()
    {
        var (client, _) = await AuthedClientAsync("chronologist");
        using var _c = client;
        await CreateNotesAsync(client, 3, "note");

        var page = (await client.GetFromJsonAsync<PagedNotes>("/notes"))!;
        var timestamps = page.Items.Select(n => n.UpdatedAt).ToList();
        Assert.Equal(timestamps.OrderByDescending(t => t), timestamps);

        var oldest = page.Items.Last();
        var update = await client.PutAsJsonAsync($"/notes/{oldest.Id}",
            new { id = oldest.Id, title = oldest.Title, content = "edited" });
        update.EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<PagedNotes>("/notes"))!;
        Assert.Equal(oldest.Id, after.Items.First().Id);
    }

    [Fact]
    public async Task GetNotes_NullUpdatedAt_SortsLast()
    {
        var (client, user) = await AuthedClientAsync("legacy_holder");
        using var _c = client;
        await CreateNotesAsync(client, 2, "note");

        // A legacy null-UpdatedAt row, inserted directly since the API always
        // stamps it. Its CreatedAt is the newest, so only the null bucket sorts it last.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetbookDbContext>();
            db.Notes.Add(new Note
            {
                Id = Guid.NewGuid(),
                Title = "legacy",
                Content = "pre-column row",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = null,
                UserId = user.Id,
            });
            await db.SaveChangesAsync();
        }

        var page = (await client.GetFromJsonAsync<PagedNotes>("/notes"))!;
        Assert.Equal(3, page.Total);
        Assert.Equal("legacy", page.Items.Last().Title);
    }

    [Fact]
    public async Task GetNotes_ClampsPageSize()
    {
        var (client, _) = await AuthedClientAsync("greedy");
        using var _c = client;
        await CreateNotesAsync(client, 3, "note");

        // pageSize above the cap is clamped to 50 (still returns all 3 here).
        var page = (await client.GetFromJsonAsync<PagedNotes>("/notes?pageSize=9999"))!;
        Assert.Equal(50, page.PageSize);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task GetNotes_OutOfRangePage_ReturnsEmptyWithRealTotal()
    {
        var (client, _) = await AuthedClientAsync("overshooter");
        using var _c = client;
        await CreateNotesAsync(client, 3, "note");

        var page = (await client.GetFromJsonAsync<PagedNotes>("/notes?page=99&pageSize=10"))!;
        Assert.Empty(page.Items);
        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.TotalPages);
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
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var updated = (await own.Content.ReadFromJsonAsync<Note>())!;
            Assert.Equal("after", updated.Title);
            Assert.True(updated.UpdatedAt > note.UpdatedAt);

            var fetched = (await owner.GetFromJsonAsync<Note>($"/notes/{note.Id}"))!;
            Assert.Equal("after", fetched.Title);
        }
    }

    [Fact]
    public async Task PutNote_StaleUpdatedAt_Returns409WithCurrentNote()
    {
        var (client, _) = await AuthedClientAsync("conflict_put");
        using var _c = client;
        var created = await client.PostAsJsonAsync("/notes", new { title = "v1", content = "c1" });
        var v1 = (await created.Content.ReadFromJsonAsync<Note>())!;

        var second = await client.PutAsJsonAsync($"/notes/{v1.Id}",
            new { id = v1.Id, title = "v2", content = "c2" });
        var v2 = (await second.Content.ReadFromJsonAsync<Note>())!;

        var conflicted = await client.PutAsJsonAsync($"/notes/{v1.Id}",
            new { id = v1.Id, title = "stale", content = "s", updatedAt = v1.UpdatedAt });
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);

        var current = (await conflicted.Content.ReadFromJsonAsync<Note>())!;
        Assert.Equal("v2", current.Title);
        Assert.Equal(v2.UpdatedAt, current.UpdatedAt);

        var fetched = (await client.GetFromJsonAsync<Note>($"/notes/{v1.Id}"))!;
        Assert.Equal("v2", fetched.Title);
    }

    [Fact]
    public async Task PutNote_CurrentUpdatedAt_Succeeds()
    {
        var (client, _) = await AuthedClientAsync("fresh_put");
        using var _c = client;
        var created = await client.PostAsJsonAsync("/notes", new { title = "v1", content = "c1" });
        var note = (await created.Content.ReadFromJsonAsync<Note>())!;

        var response = await client.PutAsJsonAsync($"/notes/{note.Id}",
            new { id = note.Id, title = "v2", content = "c2", updatedAt = note.UpdatedAt });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<Note>())!;
        Assert.Equal("v2", updated.Title);
        Assert.True(updated.UpdatedAt > note.UpdatedAt);
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
    public async Task DeleteNote_StaleUpdatedAt_Returns409AndKeepsNote()
    {
        var (client, _) = await AuthedClientAsync("conflict_del");
        using var _c = client;
        var created = await client.PostAsJsonAsync("/notes", new { title = "v1", content = "c1" });
        var v1 = (await created.Content.ReadFromJsonAsync<Note>())!;

        var second = await client.PutAsJsonAsync($"/notes/{v1.Id}",
            new { id = v1.Id, title = "v2", content = "c2" });
        var v2 = (await second.Content.ReadFromJsonAsync<Note>())!;

        var conflicted = await DeleteWithBodyAsync(client, v1.Id, v1.UpdatedAt);
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
        var current = (await conflicted.Content.ReadFromJsonAsync<Note>())!;
        Assert.Equal("v2", current.Title);

        var still = await client.GetAsync($"/notes/{v1.Id}");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        var fresh = await DeleteWithBodyAsync(client, v1.Id, v2.UpdatedAt);
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task DeleteNote_ConditionalOnLegacyNullRow_Succeeds()
    {
        var (client, user) = await AuthedClientAsync("legacy_del");
        using var _c = client;

        // A null-UpdatedAt row is never newer than a caller's base, so the delete proceeds.
        var legacyId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetbookDbContext>();
            db.Notes.Add(new Note
            {
                Id = legacyId,
                Title = "legacy",
                Content = "pre-column row",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = null,
                UserId = user.Id,
            });
            await db.SaveChangesAsync();
        }

        var response = await DeleteWithBodyAsync(client, legacyId, DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> DeleteWithBodyAsync(
        HttpClient client, Guid id, DateTime? updatedAt)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/notes/{id}")
        {
            Content = JsonContent.Create(new { updatedAt }),
        };
        return await client.SendAsync(request);
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

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

namespace netbook_service.Controllers;

public record NoteCreateDto([MaxLength(200)] string Title, [MaxLength(100_000)] string Content);

// UpdatedAt here is not a writable field: it is an optional optimistic-concurrency
// precondition (the timestamp the client based its change on), checked by IsStale.
public record NoteUpdateDto(
    Guid Id,
    [MaxLength(200)] string Title,
    [MaxLength(100_000)] string Content,
    DateTime? UpdatedAt = null
);

public record NoteDeleteDto(DateTime? UpdatedAt = null);

public record PagedResult<T>(IEnumerable<T> Items, int Page, int PageSize, int Total, int TotalPages);

[ApiController]
[Route("[controller]")]
[Authorize]
public class NotesController(NetbookDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<Note>>> GetNotes(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10
    )
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        // Most recently updated first; the leading null-bucket sort keeps legacy
        // null-UpdatedAt rows last (Postgres orders nulls first on DESC otherwise).
        var query = context.Notes
            .Where(n => n.UserId == user.Id)
            .OrderBy(n => n.UpdatedAt == null)
            .ThenByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new PagedResult<Note>(items, page, pageSize, total, totalPages);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Note>> GetNote(Guid id)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var note = await context.Notes.FindAsync(id);

        // 404 rather than 403 on a foreign note, so callers can't probe which ids exist.
        if (note == null || note.UserId != user.Id)
        {
            return NotFound();
        }

        return note;
    }

    [HttpPost]
    public async Task<ActionResult<Note>> PostNote(NoteCreateDto dto)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = dto.Title,
            Content = dto.Content,
            CreatedAt = now,
            UpdatedAt = now,
            UserId = user.Id,
        };

        context.Notes.Add(note);
        await context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetNote), new { id = note.Id }, note);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Note>> PutNote(Guid id, NoteUpdateDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest();
        }

        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var existingNote = await context.Notes.FindAsync(id);
        if (existingNote == null || existingNote.UserId != user.Id)
        {
            return NotFound();
        }

        if (IsStale(existingNote, dto.UpdatedAt))
        {
            return Conflict(existingNote);
        }

        existingNote.Title = dto.Title;
        existingNote.Content = dto.Content;
        existingNote.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        // Returns the row so a syncing client gets the fresh UpdatedAt for its next write.
        return existingNote;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteNote(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NoteDeleteDto? dto = null
    )
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var note = await context.Notes.FindAsync(id);
        if (note == null || note.UserId != user.Id)
        {
            return NotFound();
        }

        if (IsStale(note, dto?.UpdatedAt))
        {
            return Conflict(note);
        }

        context.Notes.Remove(note);
        await context.SaveChangesAsync();

        return NoContent();
    }

    // Strictly-newer, not exact-match: Postgres truncates to microseconds while
    // JSON carries .NET's 100ns ticks, so an echoed value would falsely mismatch.
    private static bool IsStale(Note note, DateTime? baseUpdatedAt) =>
        baseUpdatedAt != null && note.UpdatedAt > baseUpdatedAt;

    // The JWT "id" claim is the IAM user's UUID (User.IamId). No local row means
    // the caller is unresolved; every action treats that as 403.
    private async Task<User?> GetCurrentUserAsync()
    {
        var iamIdClaim = User.FindFirstValue("id");
        if (!Guid.TryParse(iamIdClaim, out var iamId))
        {
            return null;
        }

        return await context.Users.FirstOrDefaultAsync(u => u.IamId == iamId);
    }
}

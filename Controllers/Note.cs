using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

namespace netbook_service.Controllers;

// Write DTOs: clients only ever supply title/content (plus the id echo on
// PUT). Id, UserId, and CreatedAt are server-owned and not bindable at all.
public record NoteCreateDto([MaxLength(200)] string Title, [MaxLength(100_000)] string Content);

public record NoteUpdateDto(
    Guid Id,
    [MaxLength(200)] string Title,
    [MaxLength(100_000)] string Content
);

// One page of results plus the counts the client needs to render pagination.
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

        // Newest first, ordered server-side so pages are stable across requests.
        var query = context.Notes
            .Where(n => n.UserId == user.Id)
            .OrderByDescending(n => n.CreatedAt);

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

        // Notes belonging to other users are reported as missing rather than
        // forbidden, so callers can't probe which note ids exist.
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

        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = dto.Title,
            Content = dto.Content,
            CreatedAt = DateTime.UtcNow,
            UserId = user.Id,
        };

        context.Notes.Add(note);
        await context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetNote), new { id = note.Id }, note);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> PutNote(Guid id, NoteUpdateDto dto)
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

        existingNote.Title = dto.Title;
        existingNote.Content = dto.Content;

        await context.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteNote(Guid id)
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

        context.Notes.Remove(note);
        await context.SaveChangesAsync();

        return NoContent();
    }

    // Resolves the caller to a local User row via the "id" claim iam-service
    // puts in the JWT, which holds the IAM user's UUID (User.IamId here).
    // Rows are created only by iam-service's register-time push and the
    // sync-users backfill script — a valid JWT with no local row gets 403.
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

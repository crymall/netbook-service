using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

namespace netbook_service.Controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
public class NotesController(NetbookDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Note>>> GetNotes()
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var userId = user.Id.ToString();
        return await context.Notes.Where(n => n.UserId == userId).ToListAsync();
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
        if (note == null || note.UserId != user.Id.ToString())
        {
            return NotFound();
        }

        return note;
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<IEnumerable<Note>>> GetNotesByUser(string userId)
    {
        var user = await GetCurrentUserAsync();
        if (user == null || user.Id.ToString() != userId)
        {
            return Forbid();
        }

        var notes = await context.Notes.Where(n => n.UserId == userId).ToListAsync();

        return notes;
    }

    [HttpPost]
    public async Task<ActionResult<Note>> PostNote(Note note)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        note.Id = Guid.NewGuid();
        note.CreatedAt = DateTime.UtcNow;
        note.UserId = user.Id.ToString();

        context.Notes.Add(note);
        await context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetNote), new { id = note.Id }, note);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> PutNote(Guid id, Note note)
    {
        if (id != note.Id)
        {
            return BadRequest();
        }

        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Forbid();
        }

        var existingNote = await context.Notes.FindAsync(id);
        if (existingNote == null || existingNote.UserId != user.Id.ToString())
        {
            return NotFound();
        }

        existingNote.Title = note.Title;
        existingNote.Content = note.Content;

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
        if (note == null || note.UserId != user.Id.ToString())
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

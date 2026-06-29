using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netbook_service.Models;

namespace netbook_service.Controllers;

[ApiController]
[Route("[controller]")]
public class NotesController(NetbookDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Note>>> GetNotes()
    {
        return await context.Notes.ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Note>> GetNote(Guid id)
    {
        var note = await context.Notes.FindAsync(id);

        if (note == null)
        {
            return NotFound();
        }

        return note;
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<IEnumerable<Note>>> GetNotesByUser(string userId)
    {
        var notes = await context.Notes.Where(n => n.UserId == userId).ToListAsync();

        return notes;
    }

    [HttpPost]
    public async Task<ActionResult<Note>> PostNote(Note note)
    {
        note.Id = Guid.NewGuid();
        note.CreatedAt = DateTime.UtcNow;

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

        context.Entry(note).State = EntityState.Modified;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!NoteExists(id))
            {
                return NotFound();
            }
            else
            {
                throw;
            }
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteNote(Guid id)
    {
        var note = await context.Notes.FindAsync(id);
        if (note == null)
        {
            return NotFound();
        }

        context.Notes.Remove(note);
        await context.SaveChangesAsync();

        return NoContent();
    }

    private bool NoteExists(Guid id)
    {
        return context.Notes.Any(e => e.Id == id);
    }
}

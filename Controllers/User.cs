using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netbook_service.Attributes;
using netbook_service.Models;

namespace netbook_service.Controllers;

// The body iam-service sends when pushing a user (snake_case keys).
public record UserSyncDto(
    [property: JsonPropertyName("iam_id")] Guid IamId,
    [property: JsonPropertyName("username")] string Username
);

[ApiController]
[Route("[controller]")]
[Authorize]
public class UsersController(NetbookDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<User>>> GetUsers()
    {
        return await context.Users.ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<User>> GetUser(int id)
    {
        var user = await context.Users.FindAsync(id);

        if (user == null)
        {
            return NotFound();
        }

        return user;
    }

    // Receives user pushes from iam-service (register-time sync and the
    // backfill script). Idempotent: pushing an already-synced user returns
    // the existing row instead of erroring, so retries and re-runs are safe.
    // iam-service authenticates with x-api-key only (no JWT), hence
    // AllowAnonymous — the ApiKey filter is the auth on the sync surface.
    [AllowAnonymous]
    [ApiKey]
    [HttpPost]
    public async Task<ActionResult<User>> PostUser(UserSyncDto dto)
    {
        if (dto.IamId == Guid.Empty || string.IsNullOrWhiteSpace(dto.Username))
        {
            return BadRequest();
        }

        var existing = await context.Users.FirstOrDefaultAsync(u => u.IamId == dto.IamId);
        if (existing != null)
        {
            return Ok(existing);
        }

        var user = new User { IamId = dto.IamId, Username = dto.Username };
        context.Users.Add(user);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent push won the unique-index race on IamId; return
            // the row it created.
            context.Entry(user).State = EntityState.Detached;
            existing = await context.Users.FirstAsync(u => u.IamId == dto.IamId);
            return Ok(existing);
        }

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
    }

    // The deletion path iam-service actually calls: DELETE /users/sync/:iamId.
    [AllowAnonymous]
    [ApiKey]
    [HttpDelete("sync/{iamId:guid}")]
    public async Task<IActionResult> DeleteUserByIamId(Guid iamId)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.IamId == iamId);
        if (user == null)
        {
            return NotFound();
        }

        await RemoveUserWithNotesAsync(user);
        return NoContent();
    }

    [AllowAnonymous]
    [ApiKey]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        await RemoveUserWithNotesAsync(user);
        return NoContent();
    }

    // Note.UserId is a plain string column with no FK, so the user's notes
    // must be removed explicitly — the database won't cascade for us.
    private async Task RemoveUserWithNotesAsync(User user)
    {
        var userId = user.Id.ToString();
        await context.Notes.Where(n => n.UserId == userId).ExecuteDeleteAsync();
        context.Users.Remove(user);
        await context.SaveChangesAsync();
    }
}

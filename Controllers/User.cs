using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netbook_service.Attributes;
using netbook_service.Models;

namespace netbook_service.Controllers;

// The body iam-service sends when pushing a user (snake_case keys).
public record UserSyncDto(
    [property: JsonPropertyName("iam_id")] Guid IamId,
    [property: JsonPropertyName("username")] [MaxLength(50)] string Username
);

// This controller is a machine-to-machine sync surface for iam-service and
// the midden-infra backfill script only — no browser-facing endpoints, so no
// JWT auth; the x-api-key check is the whole story.
[ApiController]
[Route("[controller]")]
[ApiKey]
public class UsersController(NetbookDbContext context) : ControllerBase
{
    [HttpGet("{id:int}")]
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
            context.Entry(user).State = EntityState.Detached;

            // A concurrent push won the unique-index race on IamId; return
            // the row it created.
            existing = await context.Users.FirstOrDefaultAsync(u => u.IamId == dto.IamId);
            if (existing != null)
            {
                return Ok(existing);
            }

            // Otherwise the username is taken by a different IAM account —
            // a stale mirror row from a missed deletion sync. Surface it;
            // reconcile with the sync-users script rather than guessing here.
            return Conflict(new { error = $"Username '{dto.Username}' is already taken." });
        }

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
    }

    // The deletion path iam-service actually calls: DELETE /users/sync/:iamId.
    // Notes cascade at the database level via the Notes.UserId foreign key.
    [HttpDelete("sync/{iamId:guid}")]
    public async Task<IActionResult> DeleteUserByIamId(Guid iamId)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.IamId == iamId);
        if (user == null)
        {
            return NotFound();
        }

        context.Users.Remove(user);
        await context.SaveChangesAsync();
        return NoContent();
    }
}

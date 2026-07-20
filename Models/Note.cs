using System.ComponentModel.DataAnnotations;

namespace netbook_service.Models;

public class Note
{
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(100_000)]
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Null only for rows that predate the column; the API always stamps it
    // (equal to CreatedAt on create, refreshed on every update).
    public DateTime? UpdatedAt { get; set; }

    public int UserId { get; set; }
}

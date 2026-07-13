using System.ComponentModel.DataAnnotations;

namespace netbook_service.Models;

public class User
{
    public int Id { get; set; }

    public Guid IamId { get; set; }

    // Matches iam-service's VARCHAR(50) username column.
    [MaxLength(50)]
    public string Username { get; set; } = string.Empty;
}

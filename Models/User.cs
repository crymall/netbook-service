using System.ComponentModel.DataAnnotations;

namespace netbook_service.Models;

public class User
{
    [Required]
    public int Id { get; set; }

    [Required]
    public Guid IamId { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;
}

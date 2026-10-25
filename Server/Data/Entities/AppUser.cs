using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string Role { get; set; } = Shared.AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// True once the user's supplier access has been set (new users, or the one-time grant of all suppliers to users that
    /// existed before supplier access was introduced). Admins always see every supplier.
    /// </summary>
    public bool SupplierAccessConfigured { get; set; }
}

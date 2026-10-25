namespace Shared;

public class UserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = AuthRoles.Lister;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Suppliers this user may see (ignored for Admins, who see all).</summary>
    public List<string> SupplierCodes { get; set; } = new();
}

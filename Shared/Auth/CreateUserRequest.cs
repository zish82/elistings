namespace Shared;

public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
    /// <summary>Suppliers the user may see (Managers and Listers; Admins see all).</summary>
    public List<string> SupplierCodes { get; set; } = new();
}

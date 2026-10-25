using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>A supplier a (non-Admin) user may see and work with. Admins see all suppliers.</summary>
public class UserSupplierAccess
{
    public int Id { get; set; }
    public int UserId { get; set; }
    /// <summary>Supplier.Code, e.g. "DropShipMall".</summary>
    public string SupplierCode { get; set; } = string.Empty;
}

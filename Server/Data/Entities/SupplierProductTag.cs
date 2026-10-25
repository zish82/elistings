using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class SupplierProductTag
{
    public int Id { get; set; }
    public int SupplierProductId { get; set; }
    public int ProductTagId { get; set; }
}

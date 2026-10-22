using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = AuthRoles.Admin)]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;

    public UsersController(AppDbContext context, IPasswordHasher passwordHasher, ICurrentUserService currentUserService)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
    }

    private SupplierAccessService Access => new(_context, _currentUserService);

    private async Task<UserDto> ToDtoAsync(AppUser u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        Role = u.Role,
        IsActive = u.IsActive,
        CreatedAt = u.CreatedAt,
        SupplierCodes = await _context.UserSupplierAccess.Where(a => a.UserId == u.Id).Select(a => a.SupplierCode).OrderBy(c => c).ToListAsync()
    };

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> GetUsers()
    {
        var users = await _context.Users
            .OrderBy(u => u.Email)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Email = u.Email,
                Role = u.Role,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        var access = (await _context.UserSupplierAccess.AsNoTracking().ToListAsync()).ToLookup(a => a.UserId, a => a.SupplierCode);
        foreach (var u in users) u.SupplierCodes = access[u.Id].OrderBy(c => c).ToList();
        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Email and password are required.");
        }

        if (!AuthRoles.All.Contains(request.Role))
        {
            return BadRequest("Invalid role.");
        }

        if (await _context.Users.AnyAsync(u => u.Email == email))
        {
            return BadRequest("A user with this email already exists.");
        }

        var hash = _passwordHasher.HashPassword(request.Password, out var salt);
        var user = new AppUser
        {
            Email = email,
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = request.Role,
            IsActive = request.IsActive,
            SupplierAccessConfigured = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        // New users only see the suppliers ticked for them.
        await Access.SetForUserAsync(user.Id, request.SupplierCodes ?? new List<string>());

        return Ok(await ToDtoAsync(user));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return NotFound();
        }

        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        if (!AuthRoles.All.Contains(request.Role))
        {
            return BadRequest("Invalid role.");
        }

        if (await _context.Users.AnyAsync(u => u.Id != id && u.Email == email))
        {
            return BadRequest("A user with this email already exists.");
        }

        user.Email = email;
        user.Role = request.Role;
        user.IsActive = request.IsActive;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = _passwordHasher.HashPassword(request.Password, out var salt);
            user.PasswordSalt = salt;
        }

        await _context.SaveChangesAsync();
        if (request.SupplierCodes != null) await Access.SetForUserAsync(user.Id, request.SupplierCodes);

        return Ok(await ToDtoAsync(user));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

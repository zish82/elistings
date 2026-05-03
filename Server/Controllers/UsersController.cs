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

    public UsersController(AppDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

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
            IsActive = request.IsActive
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            Role = user.Role,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt
        });
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

        return Ok(new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            Role = user.Role,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt
        });
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

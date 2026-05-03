using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;
using System.Security.Claims;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;

    public AccountController(AppDbContext context, IPasswordHasher passwordHasher, ICurrentUserService currentUserService)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
    }

    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<ActionResult<SessionInfoDto>> GetSession()
    {
        var hasUsers = await _context.Users.AnyAsync();
        return Ok(new SessionInfoDto
        {
            HasUsers = hasUsers,
            IsAuthenticated = _currentUserService.IsAuthenticated,
            User = _currentUserService.GetCurrentUserDto()
        });
    }

    [AllowAnonymous]
    [HttpPost("bootstrap-admin")]
    public async Task<ActionResult<SessionInfoDto>> BootstrapAdmin([FromBody] BootstrapAdminRequest request)
    {
        if (await _context.Users.AnyAsync())
        {
            return BadRequest("Users already exist. Bootstrap is no longer available.");
        }

        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Email and password are required.");
        }

        var hash = _passwordHasher.HashPassword(request.Password, out var salt);
        var user = new AppUser
        {
            Email = email,
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = AuthRoles.Admin,
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        await SignInAsync(user);

        return Ok(new SessionInfoDto
        {
            HasUsers = true,
            IsAuthenticated = true,
            User = ToCurrentUser(user)
        });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<SessionInfoDto>> Login([FromBody] LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Email and password are required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || !user.IsActive || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            return Unauthorized("Invalid email or password.");
        }

        await SignInAsync(user);
        return Ok(new SessionInfoDto
        {
            HasUsers = true,
            IsAuthenticated = true,
            User = ToCurrentUser(user)
        });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private async Task SignInAsync(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Role, user.Role)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14)
        });
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static CurrentUserDto ToCurrentUser(AppUser user)
    {
        return new CurrentUserDto
        {
            Id = user.Id,
            Email = user.Email,
            Role = user.Role,
            CanManageUsers = string.Equals(user.Role, AuthRoles.Admin, StringComparison.OrdinalIgnoreCase),
            CanViewAllListings = string.Equals(user.Role, AuthRoles.Admin, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(user.Role, AuthRoles.Manager, StringComparison.OrdinalIgnoreCase)
        };
    }
}

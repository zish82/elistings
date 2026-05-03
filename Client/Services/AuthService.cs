using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Net;
using System.Net.Http.Json;
using Shared;

namespace Client.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly NavigationManager _navigation;

    public AuthService(HttpClient http, NavigationManager navigation)
    {
        _http = http;
        _navigation = navigation;
    }

    public SessionInfoDto? CurrentSession { get; private set; }
    public CurrentUserDto? CurrentUser => CurrentSession?.User;

    public async Task<SessionInfoDto> GetSessionAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && CurrentSession != null)
        {
            return CurrentSession;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/account/session");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        CurrentSession = await response.Content.ReadFromJsonAsync<SessionInfoDto>() ?? new SessionInfoDto();
        return CurrentSession;
    }

    public async Task<bool> EnsureAuthenticatedAsync(string? returnUrl = null)
    {
        var session = await GetSessionAsync(true);
        if (session.IsAuthenticated)
        {
            return true;
        }

        var target = session.HasUsers ? "/login" : "/setup-admin";
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            target += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";
        }
        _navigation.NavigateTo(target, replace: true);
        return false;
    }

    public async Task<bool> EnsureAdminAsync(string? returnUrl = null)
    {
        if (!await EnsureAuthenticatedAsync(returnUrl))
        {
            return false;
        }

        if (CurrentUser?.CanManageUsers == true)
        {
            return true;
        }

        _navigation.NavigateTo("/", replace: true);
        return false;
    }

    public async Task LoginAsync(LoginRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/account/login")
        {
            Content = JsonContent.Create(request)
        };
        message.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception(string.IsNullOrWhiteSpace(error) ? "Login failed." : error);
        }

        CurrentSession = await response.Content.ReadFromJsonAsync<SessionInfoDto>() ?? new SessionInfoDto();
    }

    public async Task BootstrapAdminAsync(BootstrapAdminRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/account/bootstrap-admin")
        {
            Content = JsonContent.Create(request)
        };
        message.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception(string.IsNullOrWhiteSpace(error) ? "Bootstrap failed." : error);
        }

        CurrentSession = await response.Content.ReadFromJsonAsync<SessionInfoDto>() ?? new SessionInfoDto();
    }

    public async Task LogoutAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/account/logout");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            response.EnsureSuccessStatusCode();
        }
        CurrentSession = null;
    }

    public async Task<List<UserDto>> GetUsersAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/users");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<UserDto>>() ?? new List<UserDto>();
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/users")
        {
            Content = JsonContent.Create(request)
        };
        message.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception(string.IsNullOrWhiteSpace(error) ? "Create user failed." : error);
        }
        return await response.Content.ReadFromJsonAsync<UserDto>() ?? throw new Exception("Create user failed.");
    }

    public async Task<UserDto> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"api/users/{id}")
        {
            Content = JsonContent.Create(request)
        };
        message.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception(string.IsNullOrWhiteSpace(error) ? "Update user failed." : error);
        }
        return await response.Content.ReadFromJsonAsync<UserDto>() ?? throw new Exception("Update user failed.");
    }
}

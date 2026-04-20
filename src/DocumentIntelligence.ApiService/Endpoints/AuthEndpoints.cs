using System.Security.Claims;
using System.Security.Cryptography;
using DocumentIntelligence.Infrastructure.Identity;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using DocumentIntelligence.ApiService.Hubs;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Contracts.Requests;
using DocumentIntelligence.Contracts.Responses;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .WithSummary("Register a new user account.")
            .Produces<AuthResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .AllowAnonymous();

        group.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Authenticate with email and password.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        group.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .WithSummary("Exchange a refresh token for new access and refresh tokens.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        // Fast-fail input validation before touching the database.
        var emailErrors = new List<string>();
        var passwordErrors = new List<string>();
        var displayNameErrors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256)
            emailErrors.Add("A valid email address is required (max 256 characters).");
        else if (!request.Email.Contains('@') || request.Email.Contains(' '))
            emailErrors.Add("Email address format is invalid.");

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            passwordErrors.Add("Password must be at least 8 characters.");
        else if (request.Password.Length > 256)
            passwordErrors.Add("Password must be 256 characters or fewer.");

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            displayNameErrors.Add("Display name is required.");
        else if (request.DisplayName.Length > 256)
            displayNameErrors.Add("Display name must be 256 characters or fewer.");

        var validationErrors = new Dictionary<string, string[]>();
        if (emailErrors.Count > 0) validationErrors["Email"] = [.. emailErrors];
        if (passwordErrors.Count > 0) validationErrors["Password"] = [.. passwordErrors];
        if (displayNameErrors.Count > 0) validationErrors["DisplayName"] = [.. displayNameErrors];
        if (validationErrors.Count > 0) return Results.ValidationProblem(validationErrors);

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return Results.ValidationProblem(
                result.Errors.ToDictionary(e => e.Code, e => new[] { e.Description }));
        }

        var (response, hashedRefreshToken) = GenerateAuthResponse(user, config);
        user.RefreshToken = hashedRefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Created("/api/v1/auth/me", response);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 256)
        {
            // Return same message as invalid credentials to avoid user enumeration.
            return Results.Problem("Invalid email or password.", statusCode: 401);
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Problem("Invalid email or password.", statusCode: 401);
        }

        var (response, hashedRefreshToken) = GenerateAuthResponse(user, config);
        user.RefreshToken = hashedRefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Ok(response);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshTokenRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        var hashedIncoming = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.RefreshToken)));

        var user = await userManager.Users
            .FirstOrDefaultAsync(u => u.RefreshToken == hashedIncoming);

        if (user is null || user.RefreshTokenExpiry < DateTimeOffset.UtcNow)
        {
            return Results.Problem("Invalid or expired refresh token.", statusCode: 401);
        }

        var (response, hashedRefreshToken) = GenerateAuthResponse(user, config);
        user.RefreshToken = hashedRefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Ok(response);
    }

    private static (AuthResponse Response, string HashedRefreshToken) GenerateAuthResponse(ApplicationUser user, IConfiguration config)
    {
        var jwtKey = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        var jwtIssuer = config["Jwt:Issuer"] ?? "DocumentIntelligence";
        var jwtAudience = config["Jwt:Audience"] ?? "DocumentIntelligence";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = DateTimeOffset.UtcNow.AddHours(1);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email!),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("displayName", user.DisplayName)
        };

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: expiry.UtcDateTime,
            signingCredentials: creds);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var hashedRefreshToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawRefreshToken)));

        return (new AuthResponse(
            accessToken,
            rawRefreshToken,
            expiry,
            new UserDto(user.Id, user.Email!, user.DisplayName)),
            hashedRefreshToken);
    }
}

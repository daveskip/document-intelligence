using System.Security.Claims;
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
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
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

        var response = GenerateAuthResponse(user, config);
        user.RefreshToken = response.RefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Created("/api/auth/me", response);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Problem("Invalid email or password.", statusCode: 401);
        }

        var response = GenerateAuthResponse(user, config);
        user.RefreshToken = response.RefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Ok(response);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshTokenRequest request,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        var users = userManager.Users.Where(u => u.RefreshToken == request.RefreshToken);
        var user = users.FirstOrDefault();

        if (user is null || user.RefreshTokenExpiry < DateTimeOffset.UtcNow)
        {
            return Results.Problem("Invalid or expired refresh token.", statusCode: 401);
        }

        var response = GenerateAuthResponse(user, config);
        user.RefreshToken = response.RefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await userManager.UpdateAsync(user);

        return Results.Ok(response);
    }

    private static AuthResponse GenerateAuthResponse(ApplicationUser user, IConfiguration config)
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
        var refreshToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));

        return new AuthResponse(
            accessToken,
            refreshToken,
            expiry,
            new UserDto(user.Id, user.Email!, user.DisplayName));
    }
}

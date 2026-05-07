using System.Security.Claims;
using DocumentIntelligence.Contracts.Requests;
using DocumentIntelligence.Contracts.Responses;
using DocumentIntelligence.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin")
            .WithTags("Admin")
            .RequireAuthorization("Admin");

        group.MapGet("/users", GetUsersAsync)
            .WithName("GetAdminUsers")
            .WithSummary("List all users (admin only).")
            .Produces<PagedResult<AdminUserDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPut("/users/{userId}/role", SetUserRoleAsync)
            .WithName("SetUserRole")
            .WithSummary("Assign a role to a user (admin only).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetUsersAsync(
        int page,
        int pageSize,
        UserManager<ApplicationUser> userManager,
        CancellationToken ct)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return Results.BadRequest("page must be ≥ 1 and pageSize must be between 1 and 100.");

        var totalCount = await userManager.Users.CountAsync(ct);

        var users = await userManager.Users
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = new List<AdminUserDto>(users.Count);
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            items.Add(new AdminUserDto(u.Id, u.Email!, u.DisplayName, u.CreatedAt, roles.ToList()));
        }

        return Results.Ok(new PagedResult<AdminUserDto>(items, totalCount, page, pageSize));
    }

    private static async Task<IResult> SetUserRoleAsync(
        string userId,
        SetUserRoleRequest request,
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager)
    {
        if (request.Role is not "Admin" and not "User")
            return Results.BadRequest("Role must be 'Admin' or 'User'.");

        var currentUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == currentUserId)
            return Results.Problem("You cannot change your own role.", statusCode: StatusCodes.Status403Forbidden);

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return Results.NotFound();

        var currentRoles = await userManager.GetRolesAsync(user);
        if (currentRoles.Count > 0)
            await userManager.RemoveFromRolesAsync(user, currentRoles);

        await userManager.AddToRoleAsync(user, request.Role);

        return Results.NoContent();
    }
}

using System.Security.Cryptography;
using System.Text;
using DocumentIntelligence.ApiService.Hubs;
using DocumentIntelligence.Contracts.Messages;
using Microsoft.AspNetCore.SignalR;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/internal").WithTags("Internal");

        group.MapPost("/documents/{id:guid}/notify", NotifyDocumentStatusAsync)
            .WithName("NotifyDocumentStatus")
            .WithSummary("Internal — called by the Functions processor to push document status changes via SignalR.")
            .ExcludeFromDescription()
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> NotifyDocumentStatusAsync(
        Guid id,
        DocumentStatusNotification notification,
        IHubContext<DocumentStatusHub> hubContext,
        IConfiguration config,
        HttpRequest request,
        CancellationToken ct)
    {
        var expectedKey = config["Internal:SharedKey"]
            ?? throw new InvalidOperationException("Internal:SharedKey is not configured.");

        request.Headers.TryGetValue("X-Internal-Key", out var providedKeyValues);
        var providedKey = providedKeyValues.ToString();

        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            return Results.Unauthorized();

        await hubContext.Clients
            .Group($"document-{id}")
            .SendAsync("DocumentStatusChanged", notification, ct);

        return Results.Ok();
    }
}

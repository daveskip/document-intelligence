using DocumentIntelligence.ApiService.Hubs;
using DocumentIntelligence.Contracts.Messages;
using Microsoft.AspNetCore.SignalR;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/internal").WithTags("Internal");

        group.MapPost("/documents/{id:guid}/notify", NotifyDocumentStatusAsync);

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
        var expectedKey = config["Internal:SharedKey"];
        if (!string.IsNullOrEmpty(expectedKey))
        {
            request.Headers.TryGetValue("X-Internal-Key", out var providedKey);
            if (providedKey != expectedKey)
                return Results.Unauthorized();
        }

        await hubContext.Clients
            .Group($"document-{id}")
            .SendAsync("DocumentStatusChanged", notification, ct);

        return Results.Ok();
    }
}

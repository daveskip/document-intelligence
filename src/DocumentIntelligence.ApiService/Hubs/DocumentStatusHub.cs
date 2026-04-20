using System.Security.Claims;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DocumentIntelligence.ApiService.Hubs;

[Authorize]
public class DocumentStatusHub(
    IDocumentRepository documentRepository,
    ILogger<DocumentStatusHub> logger) : Hub
{
    public async Task JoinDocumentGroup(string documentId)
    {
        if (!Guid.TryParse(documentId, out var docGuid))
            throw new HubException("Invalid document ID format.");

        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub")
            ?? throw new HubException("User is not authenticated.");

        var document = await documentRepository.GetByIdAsync(docGuid, Context.ConnectionAborted);
        if (document is null || document.UploadedByUserId != userId)
            throw new HubException("Document not found or access denied.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"document-{documentId}");
    }

    public async Task LeaveDocumentGroup(string documentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"document-{documentId}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
            logger.LogWarning(exception, "SignalR client {ConnectionId} disconnected with error.", Context.ConnectionId);
        else
            logger.LogDebug("SignalR client {ConnectionId} disconnected cleanly.", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}

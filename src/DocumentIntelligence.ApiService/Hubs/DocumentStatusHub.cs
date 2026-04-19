using DocumentIntelligence.Contracts.Messages;
using Microsoft.AspNetCore.SignalR;

namespace DocumentIntelligence.ApiService.Hubs;

public class DocumentStatusHub : Hub
{
    public async Task JoinDocumentGroup(string documentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"document-{documentId}");
    }

    public async Task LeaveDocumentGroup(string documentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"document-{documentId}");
    }
}

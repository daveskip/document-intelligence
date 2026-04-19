using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DocumentIntelligence.Contracts.Messages;

namespace DocumentIntelligence.Infrastructure.Services;

public interface IDocumentQueuePublisher
{
    Task PublishAsync(DocumentProcessingMessage message, CancellationToken ct = default);
}

public class DocumentQueuePublisher(ServiceBusClient serviceBusClient) : IDocumentQueuePublisher, IAsyncDisposable
{
    private const string QueueName = "document-processing";
    private readonly ServiceBusSender _sender = serviceBusClient.CreateSender(QueueName);

    public async Task PublishAsync(DocumentProcessingMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        var sbMessage = new ServiceBusMessage(json)
        {
            MessageId = message.DocumentId.ToString(),
            ContentType = "application/json"
        };
        await _sender.SendMessageAsync(sbMessage, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

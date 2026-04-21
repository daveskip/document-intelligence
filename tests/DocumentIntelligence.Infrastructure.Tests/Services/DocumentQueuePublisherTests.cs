using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace DocumentIntelligence.Infrastructure.Tests.Services;

public class DocumentQueuePublisherTests
{
    [Fact]
    public async Task PublishAsync_SendsMessageWithCorrectMessageId()
    {
        // Arrange
        var sender = Substitute.For<ServiceBusSender>();
        var client = Substitute.For<ServiceBusClient>();
        client.CreateSender("document-processing").Returns(sender);

        ServiceBusMessage? capturedMessage = null;
        await sender.SendMessageAsync(
            Arg.Do<ServiceBusMessage>(m => capturedMessage = m),
            Arg.Any<CancellationToken>());

        var publisher = new DocumentQueuePublisher(client);
        var message = new DocumentProcessingMessage(
            Guid.NewGuid(), "blob/path", "test.pdf", "application/pdf", "user-1");

        // Act
        await publisher.PublishAsync(message);

        // Assert
        await sender.Received(1).SendMessageAsync(
            Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
        capturedMessage.Should().NotBeNull();
        capturedMessage!.MessageId.Should().Be(message.DocumentId.ToString());
        capturedMessage.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task PublishAsync_SerializesMessageBodyAsJson()
    {
        var sender = Substitute.For<ServiceBusSender>();
        var client = Substitute.For<ServiceBusClient>();
        client.CreateSender("document-processing").Returns(sender);

        ServiceBusMessage? capturedMessage = null;
        await sender.SendMessageAsync(
            Arg.Do<ServiceBusMessage>(m => capturedMessage = m),
            Arg.Any<CancellationToken>());

        var publisher = new DocumentQueuePublisher(client);
        var docId = Guid.NewGuid();
        var message = new DocumentProcessingMessage(docId, "blob/path", "file.pdf", "application/pdf", "user-abc");

        await publisher.PublishAsync(message);

        capturedMessage.Should().NotBeNull();
        var bodyJson = capturedMessage!.Body.ToString();
        var deserialized = JsonSerializer.Deserialize<DocumentProcessingMessage>(bodyJson);
        deserialized.Should().NotBeNull();
        deserialized!.DocumentId.Should().Be(docId);
        deserialized.FileName.Should().Be("file.pdf");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var sender = Substitute.For<ServiceBusSender>();
        var client = Substitute.For<ServiceBusClient>();
        client.CreateSender("document-processing").Returns(sender);

        var publisher = new DocumentQueuePublisher(client);

        Func<Task> act = async () => await publisher.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}

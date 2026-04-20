using System.Net.Http.Json;
using System.Text.Json;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Functions.Services;
using DocumentIntelligence.Infrastructure.Data;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocumentIntelligence.Functions;

public class DocumentProcessingFunction(
    AppDbContext db,
    IDocumentRepository documentRepository,
    IBlobStorageService blobStorageService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IDocumentExtractionService extractionService,
    ILogger<DocumentProcessingFunction> logger)
{
    [Function(nameof(DocumentProcessingFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger("document-processing", Connection = "servicebus")] string messageBody,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        DocumentProcessingMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<DocumentProcessingMessage>(messageBody);
            if (message is null) throw new InvalidOperationException("Message body is null after deserialization.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Failed to deserialize Service Bus message — message will be abandoned and dead-lettered after max retries. Body preview: {BodyPreview}",
                messageBody.Length > 200 ? messageBody[..200] + "…" : messageBody);
            return; // abandon; dead-letter after max delivery count
        }

        logger.LogInformation("Processing document {DocumentId}: {FileName}", message.DocumentId, message.FileName);

        await documentRepository.UpdateStatusAsync(message.DocumentId, DocumentStatus.Processing, null, cancellationToken);

        try
        {
            var blobContent = await blobStorageService.DownloadBytesAsync(message.BlobPath, cancellationToken);
            var extractedJson = await extractionService.ExtractAsync(blobContent, message.ContentType, message.FileName, cancellationToken);

            var result = new ExtractionResult
            {
                Id = Guid.NewGuid(),
                DocumentId = message.DocumentId,
                ExtractedJson = extractedJson,
                ConfidenceScore = 0.85, // Gemma 4 does not return confidence directly
                ModelVersion = extractionService.ModelName,
                ProcessedAt = DateTimeOffset.UtcNow
            };

            db.ExtractionResults.Add(result);
            await db.SaveChangesAsync(cancellationToken);

            await documentRepository.UpdateStatusAsync(message.DocumentId, DocumentStatus.Completed, null, cancellationToken);

            await NotifyApiAsync(message.DocumentId, new DocumentStatusNotification(
                message.DocumentId,
                DocumentStatus.Completed,
                "Completed",
                null,
                new ExtractionSummary(result.ConfidenceScore, result.ModelVersion, result.ProcessedAt)),
                cancellationToken);

            logger.LogInformation("Document {DocumentId} processed successfully.", message.DocumentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process document {DocumentId}.", message.DocumentId);

            const string clientError = "Processing failed. Please try again or contact support.";
            await documentRepository.UpdateStatusAsync(message.DocumentId, DocumentStatus.Failed, clientError, cancellationToken);

            await NotifyApiAsync(message.DocumentId, new DocumentStatusNotification(
                message.DocumentId,
                DocumentStatus.Failed,
                "Failed",
                clientError,
                null),
                cancellationToken);

            throw; // Re-throw so Service Bus can dead-letter after max delivery count
        }
    }

    private async Task NotifyApiAsync(
        Guid documentId, DocumentStatusNotification notification, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("apiservice");
            var internalKey = configuration["Internal:SharedKey"];

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/internal/documents/{documentId}/notify");

            if (!string.IsNullOrEmpty(internalKey))
                request.Headers.Add("X-Internal-Key", internalKey);

            request.Content = JsonContent.Create(notification);
            var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Notify API returned {StatusCode} for document {DocumentId}",
                    response.StatusCode, documentId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify API for document {DocumentId}. SignalR update skipped.", documentId);
        }
    }
}

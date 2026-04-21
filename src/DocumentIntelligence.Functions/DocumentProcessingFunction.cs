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

            var processingStartedAt = DateTimeOffset.UtcNow;
            var extractedJson = await extractionService.ExtractAsync(blobContent, message.ContentType, message.FileName, cancellationToken);
            var durationMs = (long)(DateTimeOffset.UtcNow - processingStartedAt).TotalMilliseconds;

            var result = new ExtractionResult
            {
                Id = Guid.NewGuid(),
                DocumentId = message.DocumentId,
                ExtractedJson = extractedJson,
                ConfidenceScore = ComputeConfidenceScore(extractedJson),
                ModelVersion = extractionService.ModelName,
                ProcessedAt = DateTimeOffset.UtcNow,
                ProcessingDurationMs = durationMs
            };

            db.ExtractionResults.Add(result);
            await db.SaveChangesAsync(cancellationToken);

            await documentRepository.UpdateStatusAsync(message.DocumentId, DocumentStatus.Completed, null, cancellationToken);

            logger.LogInformation(
                "Document {DocumentId} processed in {DurationMs}ms with confidence {Confidence:P0}.",
                message.DocumentId, durationMs, result.ConfidenceScore);

            await NotifyApiAsync(message.DocumentId, new DocumentStatusNotification(
                message.DocumentId,
                DocumentStatus.Completed,
                "Completed",
                null,
                new ExtractionSummary(result.ConfidenceScore, result.ModelVersion, result.ProcessedAt, durationMs)),
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

    /// <summary>
    /// Derives a confidence score from the completeness of the extracted JSON.
    /// Score = non-null top-level fields / total top-level fields.
    /// Penalised by 30% if the response was truncated.
    /// Metadata/internal keys are excluded from the calculation.
    /// </summary>
    private static double ComputeConfidenceScore(string extractedJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(extractedJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0.0;

            // Keys that are structural/metadata — excluded from the field-completeness ratio.
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "_metadata", "_reasoning", "_truncated"
            };

            bool isTruncated = root.TryGetProperty("_truncated", out var truncatedProp)
                && truncatedProp.ValueKind == JsonValueKind.True;

            int total = 0;
            int nonNull = 0;

            foreach (var property in root.EnumerateObject())
            {
                if (excluded.Contains(property.Name)) continue;
                total++;
                if (property.Value.ValueKind != JsonValueKind.Null)
                    nonNull++;
            }

            if (total == 0) return 0.0;

            var score = Math.Round((double)nonNull / total, 2);
            return isTruncated ? Math.Round(score * 0.7, 2) : score;
        }
        catch
        {
            return 0.0;
        }
    }
}

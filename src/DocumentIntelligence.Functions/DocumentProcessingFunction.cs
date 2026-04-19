using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using UglyToad.PdfPig;

namespace DocumentIntelligence.Functions;

public class DocumentProcessingFunction(
    AppDbContext db,
    BlobServiceClient blobServiceClient,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    OllamaApiClient ollamaClient,
    ILogger<DocumentProcessingFunction> logger)
{
    private const string ModelName = "gemma4";

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
            logger.LogError(ex, "Failed to deserialize message body: {Body}", messageBody);
            return; // dead-letter
        }

        logger.LogInformation("Processing document {DocumentId}: {FileName}", message.DocumentId, message.FileName);

        // Set status to Processing
        await UpdateDocumentStatusAsync(message.DocumentId, DocumentStatus.Processing, null, cancellationToken);

        try
        {
            // Download blob
            var blobContent = await DownloadBlobAsync(message.BlobPath, cancellationToken);

            // Extract document content (text for PDFs, base64 for images)
            var (textContent, imageBase64s) = ExtractContent(blobContent, message.ContentType);

            // Run inference via Ollama Gemma 4
            var extractedJson = await ExtractFieldsWithOllamaAsync(textContent, imageBase64s, message.FileName, cancellationToken);

            // Save result
            var result = new ExtractionResult
            {
                Id = Guid.NewGuid(),
                DocumentId = message.DocumentId,
                ExtractedJson = extractedJson,
                ConfidenceScore = 0.85, // Gemma 4 does not return confidence directly
                ModelVersion = ModelName,
                ProcessedAt = DateTimeOffset.UtcNow
            };

            db.ExtractionResults.Add(result);
            await db.SaveChangesAsync(cancellationToken);

            await UpdateDocumentStatusAsync(message.DocumentId, DocumentStatus.Completed, null, cancellationToken);

            // Notify API to push SignalR update
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
            await UpdateDocumentStatusAsync(message.DocumentId, DocumentStatus.Failed, ex.Message, cancellationToken);

            await NotifyApiAsync(message.DocumentId, new DocumentStatusNotification(
                message.DocumentId,
                DocumentStatus.Failed,
                "Failed",
                ex.Message,
                null),
                cancellationToken);

            throw; // Re-throw so Service Bus can dead-letter after max delivery count
        }
    }

    private async Task<byte[]> DownloadBlobAsync(string blobPath, CancellationToken ct)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient("documents");
        var blobClient = containerClient.GetBlobClient(blobPath);
        var response = await blobClient.DownloadContentAsync(ct);
        return response.Value.Content.ToArray();
    }

    /// <summary>
    /// Extracts content from the document.
    /// For PDFs: extracts text per page (PdfPig is a text-extraction library).
    /// For images: returns base64-encoded bytes for vision inference.
    /// </summary>
    private static (string? TextContent, string[]? ImageBase64s) ExtractContent(byte[] fileContent, string contentType)
    {
        if (contentType == "application/pdf")
        {
            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(fileContent);
            foreach (var page in pdf.GetPages())
            {
                sb.AppendLine($"--- Page {page.Number} ---");
                sb.AppendLine(page.Text);
            }
            return (sb.ToString(), null);
        }

        // Image files (JPEG, PNG, TIFF) — pass to vision model as base64
        return (null, [Convert.ToBase64String(fileContent)]);
    }

    private async Task<string> ExtractFieldsWithOllamaAsync(
        string? textContent, string[]? imageBase64s, string fileName, CancellationToken ct)
    {
        var ollama = ollamaClient;

        var prompt = textContent is not null
            ? $"""
            You are an expert at extracting structured data from official government forms and documents.

            Analyze the following text extracted from "{fileName}" and extract ALL fields you can identify.
            Return ONLY a valid JSON object where:
            - Keys are the field names/labels found in the document (use camelCase, no spaces)
            - Values are the field values extracted from the document
            - If a field is blank/empty, set its value to null
            - For checkboxes, use true/false
            - For dates, use ISO 8601 format (YYYY-MM-DD) where possible

            Include a "_metadata" key with:
            - "documentType": your best guess at the form type
            - "pageCount": number of pages analyzed
            - "extractionNotes": any notes about ambiguous or low-confidence fields

            Document text:
            {textContent}

            Return only the JSON object, no explanation.
            """
            : $"""
            You are an expert at extracting structured data from official government forms and documents.

            Analyze the document image(s) from file "{fileName}" and extract ALL fields you can identify.
            Return ONLY a valid JSON object where:
            - Keys are the field names/labels found in the document (use camelCase, no spaces)
            - Values are the field values extracted from the document
            - If a field is blank/empty, set its value to null
            - For checkboxes, use true/false
            - For dates, use ISO 8601 format (YYYY-MM-DD) where possible

            Include a "_metadata" key with:
            - "documentType": your best guess at the form type
            - "pageCount": number of pages analyzed
            - "extractionNotes": any notes about ambiguous or low-confidence fields

            Return only the JSON object, no explanation.
            """;

        var messages = new List<Message>
        {
            new() { Role = ChatRole.User, Content = prompt, Images = imageBase64s }
        };

        var sb = new StringBuilder();
        await foreach (var chunk in ollama.ChatAsync(
            new OllamaSharp.Models.Chat.ChatRequest
            {
                Model = ModelName,
                Messages = messages,
                Stream = true
            }, ct))
        {
            if (chunk?.Message?.Content is not null)
                sb.Append(chunk.Message.Content);
        }

        var rawResponse = sb.ToString().Trim();

        // Strip markdown code fences if present
        if (rawResponse.StartsWith("```json"))
            rawResponse = rawResponse[7..];
        else if (rawResponse.StartsWith("```"))
            rawResponse = rawResponse[3..];
        if (rawResponse.EndsWith("```"))
            rawResponse = rawResponse[..^3];

        rawResponse = rawResponse.Trim();

        // Validate it's parseable JSON
        try
        {
            using var _ = JsonDocument.Parse(rawResponse);
        }
        catch
        {
            rawResponse = JsonSerializer.Serialize(new { raw = rawResponse, parseError = "Response was not valid JSON" });
        }

        return rawResponse;
    }

    private async Task UpdateDocumentStatusAsync(
        Guid documentId, DocumentStatus status, string? errorMessage, CancellationToken ct)
    {
        await db.Documents
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.ErrorMessage, errorMessage), ct);
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
                $"/api/internal/documents/{documentId}/notify");

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

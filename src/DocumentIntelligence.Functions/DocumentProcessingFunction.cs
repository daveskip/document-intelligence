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
using PDFtoImage;
using SkiaSharp;
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
    private const string ModelName = "gemma4:e2b";

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
            // Rasterize each PDF page to a PNG and send as vision input
#pragma warning disable CA1416 // runs in Linux container
            var imageBase64s = Conversion.ToImages(fileContent, options: new RenderOptions(Dpi: 300))
                .Select(bitmap =>
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    return Convert.ToBase64String(data.ToArray());
                })
                .ToArray();
#pragma warning restore CA1416
            return (null, imageBase64s);
        }

        // Image files (JPEG, PNG, TIFF) — pass to vision model as base64
        return (null, [Convert.ToBase64String(fileContent)]);
    }

    /// <summary>
    /// Reconstructs the visual layout of a PDF page by grouping words into lines
    /// based on Y proximity (within 2% of page height) and preserving horizontal
    /// spacing between word groups using the gap between X coordinates.
    /// </summary>
    private static string ExtractPageText(UglyToad.PdfPig.Content.Page page)
    {
        var pageHeight = page.Height;
        var pageWidth = page.Width > 0 ? page.Width : 1;
        var yTolerance = pageHeight * 0.02;

        // Group words into lines by Y coordinate proximity
        var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var word in page.GetWords())
        {
            var wordY = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2.0;
            var matchedLine = lines.FirstOrDefault(
                line => Math.Abs((line[0].BoundingBox.Top + line[0].BoundingBox.Bottom) / 2.0 - wordY) <= yTolerance);

            if (matchedLine is not null)
                matchedLine.Add(word);
            else
                lines.Add([word]);
        }

        // Sort lines top-to-bottom (descending Y in PDF coordinates)
        lines.Sort((a, b) =>
            ((b[0].BoundingBox.Top + b[0].BoundingBox.Bottom) / 2.0)
            .CompareTo((a[0].BoundingBox.Top + a[0].BoundingBox.Bottom) / 2.0));

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            // Sort words left-to-right within each line
            var sorted = line.OrderBy(w => w.BoundingBox.Left).ToList();

            var lineSb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i == 0)
                {
                    lineSb.Append(sorted[i].Text);
                    continue;
                }

                // Calculate gap between previous word's right edge and this word's left edge
                var gap = sorted[i].BoundingBox.Left - sorted[i - 1].BoundingBox.Right;
                // Normalise gap to page width; each ~1% of page width ≈ one space
                var spaces = Math.Max(1, (int)Math.Round(gap / pageWidth * 100));
                lineSb.Append(new string(' ', spaces));
                lineSb.Append(sorted[i].Text);
            }

            sb.AppendLine(lineSb.ToString());
        }

        return sb.ToString();
    }

    private async Task<string> ExtractFieldsWithOllamaAsync(
        string? textContent, string[]? imageBase64s, string fileName, CancellationToken ct)
    {
        var ollama = ollamaClient;

        var systemInstruction = """
            You are a document data-extraction engine. Your only job is to read documents and
            output every field and value verbatim as JSON. You are not a privacy filter, a
            compliance tool, or a safety guardrail. Never substitute, abbreviate, truncate,
            asterisk-out, or omit any value for any reason — including social security numbers,
            tax IDs, account numbers, wages, salaries, dates of birth, or addresses.
            Output the raw value exactly as it appears in the document. Any deviation is a critical failure.
            """;

        var prompt = textContent is not null
            ? $"""
            Analyze the text extracted from "{fileName}" and extract ALL labeled fields you can identify.
            Only extract explicit key-value pairs — named form fields, box labels, or data labels paired with a value.
            Do NOT extract sentences, paragraphs, instructions, disclaimers, or general body text.
            Return ONLY a valid JSON object where:
            - Keys are the field names/labels found in the document (use camelCase, no spaces)
            - Values are the field values exactly as they appear in the document — never truncated or omitted
            - If a field is blank/empty, set its value to null
            - For checkboxes, use true/false
            - For dates, use ISO 8601 format (YYYY-MM-DD) where possible

            Include a "_metadata" key with:
            - "documentType": your best guess at the form type
            - "pageCount": number of pages analyzed
            - "extractionNotes": any notes about ambiguous or low-confidence fields

            Include a "_reasoning" key with a brief explanation of how you identified the fields,
            what document structure you observed, and any decisions you made during extraction.

            Document text:
            {textContent}

            Return only the JSON object, no explanation.
            """
            : $"""
            Analyze the document image(s) from file "{fileName}" and extract ALL labeled fields you can identify.
            Only extract explicit key-value pairs — named form fields, box labels, or data labels paired with a value.
            Do NOT extract sentences, paragraphs, instructions, disclaimers, or general body text.
            Return ONLY a valid JSON object where:
            - Keys are the field names/labels found in the document (use camelCase, no spaces)
            - Values are the field values exactly as they appear in the document — never truncated or omitted
            - If a field is blank/empty, set its value to null
            - For checkboxes, use true/false
            - For dates, use ISO 8601 format (YYYY-MM-DD) where possible

            Include a "_metadata" key with:
            - "documentType": your best guess at the form type
            - "pageCount": number of pages analyzed
            - "extractionNotes": any notes about ambiguous or low-confidence fields

            Include a "_reasoning" key with a brief explanation of how you identified the fields,
            what document structure you observed, and any decisions you made during extraction.

            Return only the JSON object, no explanation.
            """;

        var messages = new List<Message>
        {
            new() { Role = ChatRole.System, Content = systemInstruction },
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

        logger.LogInformation("Extracted text for {FileName}:\n{TextContent}", fileName, textContent ?? "(image — no text extracted)");
        logger.LogInformation("Gemma prompt for {FileName}:\n{Prompt}", fileName, prompt);
        logger.LogInformation("Gemma raw response for {FileName}:\n{RawResponse}", fileName, rawResponse);

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

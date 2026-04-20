using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using PDFtoImage;
using SkiaSharp;

namespace DocumentIntelligence.Functions.Services;

public interface IDocumentExtractionService
{
    /// <summary>Identifies the model used for extraction (stored on the saved result record).</summary>
    string ModelName { get; }

    /// <summary>
    /// Extracts structured JSON fields from a document using Ollama vision inference.
    /// Handles both PDF (rasterised to images) and direct image formats.
    /// </summary>
    Task<string> ExtractAsync(byte[] fileContent, string contentType, string fileName, CancellationToken ct = default);
}

public class DocumentExtractionService(
    OllamaApiClient ollamaClient,
    ILogger<DocumentExtractionService> logger) : IDocumentExtractionService
{
    public string ModelName => "gemma4:e4b";

    public async Task<string> ExtractAsync(byte[] fileContent, string contentType, string fileName, CancellationToken ct = default)
    {
        var (textContent, imageBase64s) = ExtractContent(fileContent, contentType);
        return await RunOllamaInferenceAsync(textContent, imageBase64s, fileName, ct);
    }

    // ── Content extraction ────────────────────────────────────────────────

    /// <summary>
    /// Converts document bytes into vision-model inputs.
    /// PDFs are rasterised page-by-page; images are base64-encoded directly.
    /// </summary>
    private static (string? TextContent, string[]? ImageBase64s) ExtractContent(byte[] fileContent, string contentType)
    {
        if (contentType == "application/pdf")
        {
            // Rasterise each PDF page to PNG at 300 DPI and send as vision input.
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

        // Image files (JPEG, PNG, TIFF, etc.) — pass directly as base64.
        return (null, [Convert.ToBase64String(fileContent)]);
    }

    // ── Ollama inference ──────────────────────────────────────────────────

    private async Task<string> RunOllamaInferenceAsync(
        string? textContent, string[]? imageBase64s, string fileName, CancellationToken ct)
    {
        const string systemInstruction = """
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
        // Guard against Ollama hanging indefinitely on large or complex documents.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        await foreach (var chunk in ollamaClient.ChatAsync(
            new ChatRequest { Model = ModelName, Messages = messages, Stream = true }, timeoutCts.Token))
        {
            if (chunk?.Message?.Content is not null)
                sb.Append(chunk.Message.Content);
        }

        var rawResponse = sb.ToString().Trim();

        // Log the prompt template structure at Debug level — no document data included.
        logger.LogDebug("Gemma prompt dispatched for {FileName} (input mode: {InputMode})",
            fileName, textContent is not null ? "text" : "image");

        // Strip markdown code fences if present.
        if (rawResponse.StartsWith("```json"))
            rawResponse = rawResponse[7..];
        else if (rawResponse.StartsWith("```"))
            rawResponse = rawResponse[3..];
        if (rawResponse.EndsWith("```"))
            rawResponse = rawResponse[..^3];

        rawResponse = rawResponse.Trim();

        // Validate JSON; wrap raw output if unparseable.
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            // Log extracted field names (keys only — no values) to avoid logging PII.
            var fieldNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            logger.LogInformation(
                "Gemma extraction for {FileName}: {FieldCount} fields extracted, response length {CharCount} chars. Fields: [{Fields}]",
                fileName, fieldNames.Length, rawResponse.Length, string.Join(", ", fieldNames));
        }
        catch
        {
            rawResponse = JsonSerializer.Serialize(new { raw = rawResponse, parseError = "Response was not valid JSON" });
        }

        return rawResponse;
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
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
    // Previously used "gemma4:e4b" or "qwen2.5vl:7b".
    public string ModelName => "qwen2.5vl:7b";

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
            // Rasterise each PDF page to PNG at 96 DPI — sufficient for OCR/vision models
            // and keeps image size small enough to fit in the model's context window.
            // 300 DPI produces ~2550×3300px images that overwhelm the context budget.
#pragma warning disable CA1416 // runs in Linux container
            var imageBase64s = Conversion.ToImages(fileContent, options: new RenderOptions(Dpi: 96))
                .Select(bitmap =>
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 85);
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
            You are an expert data extraction assistant specialized in financial documents, tax forms, payroll, and HR/employee records.

            1. First, determine the exact document type.
            2. Extract EVERY relevant piece of information into clean, structured JSON.
            3. Be precise with numbers, dates, and monetary values.
            4. If a field is not present, use null.
            5. Preserve original formatting for tables (convert to array of objects).

            Common fields to always include when present:
            - document_type, document_date, tax_year or period, company_name or employer_name
            - employee_name or individual_name, employee_id, ssn_or_tax_id (if present)

            Financial / Tax document fields:
            - wages, federal_tax_withheld, state_tax_withheld, social_security_tax, medicare_tax
            - total_income, deductions, credits, refunds, account_numbers, transaction_details (as array)

            HR / Payroll / Employee record fields:
            - position_title, department, hire_date, termination_date, salary_or_hourly_rate
            - gross_pay, net_pay, deductions_breakdown, benefits_details, leave_balances
            - performance_notes, review_date, compliance_info

            Output ONLY valid JSON — no explanations, no markdown. Example structure:
            {
              "document_type": "W-2" | "payroll_summary" | "hr_employee_record" | "bank_statement" | "1099" | "benefits_enrollment" | ...,
              "document_date": "YYYY-MM-DD",
              "company_name": "...",
              "employee_name": "...",
              "extracted_data": {
                "wages": 12345.67,
                "federal_tax_withheld": 2345.00,
                ...
              },
              "tables": [ ... ]
            }
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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));

        logger.LogInformation("Sending request to Ollama model {Model} for {FileName}", ModelName, fileName);

        // Verify Ollama is reachable before dispatching the full vision request.
        if (!await ollamaClient.IsRunningAsync(timeoutCts.Token))
            throw new InvalidOperationException("Ollama is not reachable.");

        // Stream = true so we get incremental chunks; collect manually to tolerate
        // streams that end without a Done=true chunk (known issue with some models + Format=json).
        var chatRequest = new ChatRequest
        {
            Model = ModelName,
            Messages = messages,
            Stream = true,
            Options = new RequestOptions
            {
                NumCtx = 16384,
                NumPredict = -1,  // No cap — use remaining context window
            }
        };

        try
        {
            await foreach (var chunk in ollamaClient.ChatAsync(chatRequest, timeoutCts.Token).ConfigureAwait(false))
            {
                if (chunk?.Message?.Content is { } content)
                    sb.Append(content);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Ollama returned an error (status {(int?)ex.StatusCode}) while processing {fileName}. " +
                $"If 500, try reducing NumCtx (currently {chatRequest.Options?.NumCtx}) — the model may be out of VRAM.", ex);
        }

        var response = sb.ToString();
        logger.LogInformation("Ollama response received for {FileName}: content_len={Len}",
            fileName, response.Length);

        var rawResponse = response.Trim();

        // Log the prompt template structure at Debug level — no document data included.
        logger.LogDebug("Qwen prompt dispatched for {FileName} (input mode: {InputMode})",
            fileName, textContent is not null ? "text" : "image");

        // Strip markdown code fences if present.
        if (rawResponse.StartsWith("```json"))
            rawResponse = rawResponse[7..];
        else if (rawResponse.StartsWith("```"))
            rawResponse = rawResponse[3..];
        if (rawResponse.EndsWith("```"))
            rawResponse = rawResponse[..^3];

        rawResponse = rawResponse.Trim();

        // Validate JSON; try to salvage truncated output if unparseable.
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            // Log extracted field names (keys only — no values) to avoid logging PII.
            var fieldNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            logger.LogInformation(
                "Qwen extraction for {FileName}: {FieldCount} fields extracted, response length {CharCount} chars. Fields: [{Fields}]",
                fileName, fieldNames.Length, rawResponse.Length, string.Join(", ", fieldNames));
        }
        catch
        {
            var salvaged = TrySalvageTruncatedJson(rawResponse);
            if (salvaged is not null)
            {
                logger.LogWarning(
                    "Qwen response for {FileName} was truncated ({OrigLen} chars). Salvaged {SalvLen} chars of valid JSON.",
                    fileName, rawResponse.Length, salvaged.Length);
                rawResponse = salvaged;
            }
            else
            {
                logger.LogWarning("Qwen response for {FileName} was not valid JSON ({CharCount} chars): {RawResponse}",
                    fileName, rawResponse.Length, rawResponse);
                rawResponse = JsonSerializer.Serialize(new { raw = rawResponse, parseError = "Response was not valid JSON" });
            }
        }

        return rawResponse;
    }

    /// <summary>
    /// Attempts to recover a valid JSON object from a response truncated mid-stream.
    /// Walks the input tracking nesting depth to find the last complete top-level field,
    /// truncates there, closes the root object, and appends a <c>_truncated</c> flag.
    /// Returns <c>null</c> if no valid partial object can be recovered.
    /// </summary>
    private static string? TrySalvageTruncatedJson(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{')) return null;

        // Walk the string tracking depth and string context to find commas at depth 1
        // (i.e., separators between root-level fields of the JSON object).
        int depth = 0;
        bool inString = false;
        bool escape = false;
        int lastDepth1Comma = -1;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ',' && depth == 1) lastDepth1Comma = i;
        }

        if (lastDepth1Comma < 0) return null;

        var candidate = trimmed[..lastDepth1Comma] + ", \"_truncated\": true}";
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch
        {
            return null;
        }
    }
}

using System.Security.Claims;
using DocumentIntelligence.Contracts.Responses;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using DocumentIntelligence.Contracts.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentIntelligence.ApiService.Endpoints;

public static class DocumentEndpoints
{
    private static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png", "image/tiff"];
    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents")
            .WithTags("Documents")
            .RequireAuthorization();

        group.MapPost("/", UploadDocumentAsync).DisableAntiforgery().RequireRateLimiting("upload")
            .WithName("UploadDocument")
            .WithSummary("Upload a document for AI extraction.")
            .Produces<DocumentDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/", GetDocumentsAsync)
            .WithName("GetDocuments")
            .WithSummary("Get a paged list of the authenticated user's documents.")
            .Produces<PagedResult<DocumentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetDocumentAsync)
            .WithName("GetDocument")
            .WithSummary("Get document details by ID.")
            .Produces<DocumentDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/file", GetDocumentFileAsync)
            .WithName("GetDocumentFile")
            .WithSummary("Download the original uploaded file.")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/results", GetExtractionResultAsync)
            .WithName("GetExtractionResult")
            .WithSummary("Get the AI extraction result for a document.")
            .Produces<ExtractionResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteDocumentAsync)
            .WithName("DeleteDocument")
            .WithSummary("Delete a document and its associated blob storage file.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/requeue", RequeueDocumentAsync)
            .WithName("RequeueDocument")
            .WithSummary("Requeue a failed document for re-processing.")
            .Produces<DocumentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> UploadDocumentAsync(
        IFormFile file,
        IBlobStorageService blobService,
        IDocumentRepository documentRepo,
        IDocumentQueuePublisher queuePublisher,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.BadRequest("File is empty.");

        if (file.Length > MaxFileSizeBytes)
            return Results.BadRequest($"File exceeds maximum size of {MaxFileSizeBytes / 1024 / 1024} MB.");

        if (!AllowedContentTypes.Contains(file.ContentType))
            return Results.BadRequest($"Unsupported file type: {file.ContentType}");

        // Sanitize filename: strip directory components and block path traversal.
        var safeFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Contains(".."))
            return Results.BadRequest("Invalid file name.");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("User ID claim not found.");

        await using var stream = file.OpenReadStream();
        var blobPath = await blobService.UploadAsync(stream, safeFileName, file.ContentType, ct);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            FileName = safeFileName,
            BlobPath = blobPath,
            ContentType = file.ContentType,
            FileSize = file.Length,
            Status = DocumentStatus.Pending,
            UploadedByUserId = userId,
            UploadedAt = DateTimeOffset.UtcNow
        };

        await documentRepo.AddAsync(document, ct);
        await documentRepo.SaveChangesAsync(ct);

        await queuePublisher.PublishAsync(new DocumentProcessingMessage(
            document.Id,
            document.BlobPath,
            document.FileName,
            document.ContentType,
            document.UploadedByUserId), ct);

        return Results.Created($"/api/documents/{document.Id}", MapToDto(document));
    }

    private static async Task<IResult> GetDocumentsAsync(
        IDocumentRepository documentRepo,
        ClaimsPrincipal user,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1)
            return Results.BadRequest("Page must be 1 or greater.");
        if (pageSize is < 1 or > 100)
            return Results.BadRequest("Page size must be between 1 and 100.");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;

        var (items, totalCount) = await documentRepo.GetPagedAsync(userId, page, pageSize, ct);
        var dtos = items.Select(MapToDto).ToList();

        return Results.Ok(new PagedResult<DocumentDto>(dtos, totalCount, page, pageSize));
    }

    private static async Task<IResult> GetDocumentAsync(
        Guid id,
        IDocumentRepository documentRepo,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var document = await documentRepo.GetByIdAsync(id, ct);
        if (document is null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;
        if (document.UploadedByUserId != userId) return Results.Forbid();

        return Results.Ok(MapToDetailDto(document));
    }

    private static async Task<IResult> GetDocumentFileAsync(
        Guid id,
        IDocumentRepository documentRepo,
        IBlobStorageService blobService,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var document = await documentRepo.GetByIdAsync(id, ct);
        if (document is null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;
        if (document.UploadedByUserId != userId) return Results.Forbid();

        var stream = await blobService.DownloadAsync(document.BlobPath, ct);
        return Results.Stream(stream, document.ContentType, document.FileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> GetExtractionResultAsync(
        Guid id,
        IDocumentRepository documentRepo,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var document = await documentRepo.GetByIdAsync(id, ct);
        if (document is null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;
        if (document.UploadedByUserId != userId) return Results.Forbid();

        if (document.ExtractionResult is null)
            return Results.NotFound("Extraction result not yet available.");

        return Results.Ok(MapToExtractionDto(document.ExtractionResult));
    }

    private static async Task<IResult> DeleteDocumentAsync(
        Guid id,
        IDocumentRepository documentRepo,
        IBlobStorageService blobService,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var document = await documentRepo.GetByIdAsync(id, ct);
        if (document is null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;
        if (document.UploadedByUserId != userId) return Results.Forbid();

        await blobService.DeleteAsync(document.BlobPath, ct);
        await documentRepo.DeleteAsync(id, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RequeueDocumentAsync(
        Guid id,
        IDocumentRepository documentRepo,
        IDocumentQueuePublisher queuePublisher,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var document = await documentRepo.GetByIdAsync(id, ct);
        if (document is null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")!;
        if (document.UploadedByUserId != userId) return Results.Forbid();

        if (document.Status != DocumentStatus.Failed)
            return Results.BadRequest("Only failed documents can be requeued.");

        await documentRepo.UpdateStatusAsync(id, DocumentStatus.Pending, null, ct);

        await queuePublisher.PublishAsync(new DocumentProcessingMessage(
            document.Id,
            document.BlobPath,
            document.FileName,
            document.ContentType,
            document.UploadedByUserId), ct);

        document.Status = DocumentStatus.Pending;
        document.ErrorMessage = null;
        return Results.Accepted($"/api/v1/documents/{id}", MapToDto(document));
    }

    private static DocumentDto MapToDto(Document d) => new(
        d.Id,
        d.FileName,
        d.ContentType,
        d.FileSize,
        d.Status,
        d.Status.ToString(),
        d.UploadedAt,
        d.ErrorMessage);

    private static DocumentDetailDto MapToDetailDto(Document d) => new(
        d.Id,
        d.FileName,
        d.ContentType,
        d.FileSize,
        d.Status,
        d.Status.ToString(),
        d.UploadedAt,
        d.ErrorMessage,
        d.ExtractionResult is not null ? MapToExtractionDto(d.ExtractionResult) : null);

    private static ExtractionResultDto MapToExtractionDto(Domain.Entities.ExtractionResult r) => new(
        r.Id,
        r.ExtractedJson,
        r.ConfidenceScore,
        r.ModelVersion,
        r.ProcessedAt,
        r.ProcessingDurationMs);
}

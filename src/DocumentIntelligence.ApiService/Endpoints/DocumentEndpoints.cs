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
        var group = app.MapGroup("/api/documents")
            .WithTags("Documents")
            .RequireAuthorization();

        group.MapPost("/", UploadDocumentAsync).DisableAntiforgery();
        group.MapGet("/", GetDocumentsAsync);
        group.MapGet("/{id:guid}", GetDocumentAsync);
        group.MapGet("/{id:guid}/file", GetDocumentFileAsync);
        group.MapGet("/{id:guid}/results", GetExtractionResultAsync);
        group.MapDelete("/{id:guid}", DeleteDocumentAsync);

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

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("User ID claim not found.");

        await using var stream = file.OpenReadStream();
        var blobPath = await blobService.UploadAsync(stream, file.FileName, file.ContentType, ct);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            FileName = file.FileName,
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
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

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
        r.ProcessedAt);
}

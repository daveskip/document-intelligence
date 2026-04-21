using DocumentIntelligence.Domain.Enums;

namespace DocumentIntelligence.Contracts.Responses;

public record DocumentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long FileSize,
    DocumentStatus Status,
    string StatusLabel,
    DateTimeOffset UploadedAt,
    string? ErrorMessage);

public record DocumentDetailDto(
    Guid Id,
    string FileName,
    string ContentType,
    long FileSize,
    DocumentStatus Status,
    string StatusLabel,
    DateTimeOffset UploadedAt,
    string? ErrorMessage,
    ExtractionResultDto? ExtractionResult);

public record ExtractionResultDto(
    Guid Id,
    string ExtractedJson,
    double ConfidenceScore,
    string ModelVersion,
    DateTimeOffset ProcessedAt,
    long ProcessingDurationMs);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

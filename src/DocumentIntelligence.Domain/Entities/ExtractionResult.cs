namespace DocumentIntelligence.Domain.Entities;

public class ExtractionResult
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string ExtractedJson { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ErrorMessage { get; set; }

    public Document Document { get; set; } = null!;
}

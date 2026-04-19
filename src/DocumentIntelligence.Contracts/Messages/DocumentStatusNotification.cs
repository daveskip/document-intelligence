using DocumentIntelligence.Domain.Enums;

namespace DocumentIntelligence.Contracts.Messages;

public record DocumentStatusNotification(
    Guid DocumentId,
    DocumentStatus Status,
    string StatusLabel,
    string? ErrorMessage,
    ExtractionSummary? ExtractionSummary);

public record ExtractionSummary(double ConfidenceScore, string ModelVersion, DateTimeOffset ProcessedAt);

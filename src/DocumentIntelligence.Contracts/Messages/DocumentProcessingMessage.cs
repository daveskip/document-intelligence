namespace DocumentIntelligence.Contracts.Messages;

public record DocumentProcessingMessage(
    Guid DocumentId,
    string BlobPath,
    string FileName,
    string ContentType,
    string UploadedByUserId);

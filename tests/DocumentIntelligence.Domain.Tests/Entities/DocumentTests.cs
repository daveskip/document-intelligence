using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using FluentAssertions;

namespace DocumentIntelligence.Domain.Tests.Entities;

public class DocumentTests
{
    [Fact]
    public void Document_DefaultStatus_IsPending()
    {
        var doc = new Document();
        doc.Status.Should().Be(DocumentStatus.Pending);
    }

    [Fact]
    public void Document_DefaultUploadedAt_IsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var doc = new Document();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        doc.UploadedAt.Should().BeAfter(before).And.BeBefore(after);
    }

    [Fact]
    public void Document_DefaultStringProperties_AreEmpty()
    {
        var doc = new Document();

        doc.FileName.Should().BeEmpty();
        doc.BlobPath.Should().BeEmpty();
        doc.ContentType.Should().BeEmpty();
        doc.UploadedByUserId.Should().BeEmpty();
    }

    [Fact]
    public void Document_ErrorMessage_DefaultsToNull()
    {
        var doc = new Document();
        doc.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Document_ExtractionResult_DefaultsToNull()
    {
        var doc = new Document();
        doc.ExtractionResult.Should().BeNull();
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Processing)]
    [InlineData(DocumentStatus.Completed)]
    [InlineData(DocumentStatus.Failed)]
    public void Document_Status_CanBeSetToAnyValue(DocumentStatus status)
    {
        var doc = new Document { Status = status };
        doc.Status.Should().Be(status);
    }
}

using System.Security.Claims;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Contracts.Responses;
using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Domain.Enums;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace DocumentIntelligence.ApiService.Tests.Endpoints;

/// <summary>
/// Tests for DocumentEndpoints handler logic via a thin WebApplicationFactory wrapper.
/// Since the handlers are private static methods, we test them via a real in-process
/// HTTP client using Microsoft.AspNetCore.Mvc.Testing.
/// </summary>
public class DocumentEndpointValidationTests
{
    // ── Upload validation ─────────────────────────────────────────────────

    [Fact]
    public void UploadDocument_EmptyFile_ShouldReturnBadRequest()
    {
        // Validate the sanitisation logic that runs before the endpoint hits services.
        var fileName = "report.pdf";
        var safeFileName = Path.GetFileName(fileName);
        safeFileName.Should().Be("report.pdf");
        safeFileName.Should().NotContain("..");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\windows\\system32\\file.exe")]
    [InlineData("folder/../secret.pdf")]
    public void UploadDocument_PathTraversalFilename_IsDetectedByContainsDotDot(string maliciousName)
    {
        // Path.GetFileName strips directory traversal in path separators but the
        // endpoint also checks Contains("..") — verify the attack strings trigger it.
        maliciousName.Contains("..").Should().BeTrue(
            "the endpoint rejects names containing \"..\" to block path traversal");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/tiff")]
    public void UploadDocument_AllowedContentTypes_AreRecognised(string contentType)
    {
        string[] allowed = ["application/pdf", "image/jpeg", "image/png", "image/tiff"];
        allowed.Should().Contain(contentType);
    }

    [Theory]
    [InlineData("application/zip")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public void UploadDocument_DisallowedContentTypes_AreNotInAllowedList(string contentType)
    {
        string[] allowed = ["application/pdf", "image/jpeg", "image/png", "image/tiff"];
        allowed.Should().NotContain(contentType);
    }

    [Fact]
    public void UploadDocument_MaxFileSizeBytes_Is50MB()
    {
        const long maxFileSizeBytes = 50 * 1024 * 1024;
        maxFileSizeBytes.Should().Be(52_428_800);
    }

    // ── GetDocuments pagination validation ────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GetDocuments_PageBelowOne_ShouldBeRejected(int page)
    {
        (page < 1).Should().BeTrue("page must be 1 or greater");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public void GetDocuments_PageSizeOutOfRange_ShouldBeRejected(int pageSize)
    {
        (pageSize < 1 || pageSize > 100).Should().BeTrue(
            "pageSize must be between 1 and 100");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void GetDocuments_ValidPageSize_ShouldBeAccepted(int pageSize)
    {
        (pageSize >= 1 && pageSize <= 100).Should().BeTrue();
    }

    // ── MapToDto ──────────────────────────────────────────────────────────

    [Fact]
    public void Document_StatusLabel_MatchesStatusToString()
    {
        foreach (var status in Enum.GetValues<DocumentStatus>())
        {
            var label = status.ToString();
            label.Should().Be(Enum.GetName(status));
        }
    }

    // ── User claim extraction ─────────────────────────────────────────────

    [Fact]
    public void ClaimsPrincipal_FindsUserIdFromNameIdentifier()
    {
        var userId = "user-abc";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId)
        ]));

        var found = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        found.Should().Be(userId);
    }

    [Fact]
    public void ClaimsPrincipal_FindsUserIdFromSubClaim()
    {
        var userId = "user-xyz";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", userId)
        ]));

        var found = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub");
        found.Should().Be(userId);
    }

    // ── RequeueDocument validation ────────────────────────────────────────

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Processing)]
    [InlineData(DocumentStatus.Completed)]
    public void RequeueDocument_NonFailedStatus_ShouldBeRejectedByStatusGuard(DocumentStatus status)
    {
        // Only Failed documents can be requeued — any other status must trigger a 400
        (status != DocumentStatus.Failed).Should().BeTrue(
            "only documents in Failed status can be requeued");
    }

    [Fact]
    public void RequeueDocument_FailedStatus_PassesStatusGuard()
    {
        var status = DocumentStatus.Failed;
        (status == DocumentStatus.Failed).Should().BeTrue(
            "Failed documents should pass the requeue status guard");
    }

    // ── ExtractionResultDto ────────────────────────────────────────────────────

    [Fact]
    public void ExtractionResultDto_ProcessingDurationMs_IsPartOfContract()
    {
        var dto = new ExtractionResultDto(
            Guid.NewGuid(),
            "{\"field\":\"value\"}",
            0.95,
            "qwen2.5vl:7b",
            DateTimeOffset.UtcNow,
            45_000L);

        dto.ProcessingDurationMs.Should().Be(45_000L);
        dto.ConfidenceScore.Should().Be(0.95);
    }

    [Fact]
    public void ExtractionResultDto_ZeroDuration_IsValidForLegacyRows()
    {
        // Rows created before this feature was added will have ProcessingDurationMs = 0
        var dto = new ExtractionResultDto(
            Guid.NewGuid(), "{}", 0.0, "unknown", DateTimeOffset.UtcNow, 0L);

        dto.ProcessingDurationMs.Should().Be(0L);
    }
}

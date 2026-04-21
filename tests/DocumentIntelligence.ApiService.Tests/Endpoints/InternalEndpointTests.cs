using System.Security.Cryptography;
using System.Text;
using DocumentIntelligence.Contracts.Messages;
using DocumentIntelligence.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace DocumentIntelligence.ApiService.Tests.Endpoints;

public class InternalEndpointKeyTests
{
    private const string ValidKey = "test-internal-key-abc123";

    // ── Key comparison logic ──────────────────────────────────────────────

    [Fact]
    public void FixedTimeEquals_SameKey_ReturnsTrue()
    {
        var expected = Encoding.UTF8.GetBytes(ValidKey);
        var provided = Encoding.UTF8.GetBytes(ValidKey);

        CryptographicOperations.FixedTimeEquals(expected, provided).Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_WrongKey_ReturnsFalse()
    {
        var expected = Encoding.UTF8.GetBytes(ValidKey);
        var provided = Encoding.UTF8.GetBytes("wrong-key");

        CryptographicOperations.FixedTimeEquals(expected, provided).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_EmptyVsNonEmpty_ReturnsFalse()
    {
        var expected = Encoding.UTF8.GetBytes(ValidKey);
        var provided = Encoding.UTF8.GetBytes(string.Empty);

        CryptographicOperations.FixedTimeEquals(expected, provided).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_BothEmpty_ReturnsTrue()
    {
        var a = Encoding.UTF8.GetBytes(string.Empty);
        var b = Encoding.UTF8.GetBytes(string.Empty);

        CryptographicOperations.FixedTimeEquals(a, b).Should().BeTrue();
    }

    // ── Configuration guard ───────────────────────────────────────────────

    [Fact]
    public void InternalSharedKey_MissingFromConfig_ShouldThrowInvalidOperation()
    {
        IConfiguration config = new ConfigurationBuilder().Build(); // no keys set
        var key = config["Internal:SharedKey"];

        var act = () =>
        {
            var resolved = key ?? throw new InvalidOperationException("Internal:SharedKey is not configured.");
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Internal:SharedKey*");
    }

    [Fact]
    public void InternalSharedKey_PresentInConfig_ReturnsValue()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Internal:SharedKey"] = ValidKey
            })
            .Build();

        var key = config["Internal:SharedKey"]
            ?? throw new InvalidOperationException("Internal:SharedKey is not configured.");

        key.Should().Be(ValidKey);
    }

    // ── DocumentStatusNotification content ───────────────────────────────

    [Fact]
    public void DocumentStatusNotification_CompletedWithSummary_IsWellFormed()
    {
        var notification = new DocumentStatusNotification(
            Guid.NewGuid(),
            DocumentStatus.Completed,
            "Completed",
            null,
            new ExtractionSummary(0.85, "qwen2.5vl:7b", DateTimeOffset.UtcNow));

        notification.Status.Should().Be(DocumentStatus.Completed);
        notification.StatusLabel.Should().Be("Completed");
        notification.ErrorMessage.Should().BeNull();
        notification.ExtractionSummary.Should().NotBeNull();
    }

    [Fact]
    public void DocumentStatusNotification_FailedWithError_IsWellFormed()
    {
        var notification = new DocumentStatusNotification(
            Guid.NewGuid(),
            DocumentStatus.Failed,
            "Failed",
            "Processing failed. Please try again or contact support.",
            null);

        notification.Status.Should().Be(DocumentStatus.Failed);
        notification.ErrorMessage.Should().NotBeNullOrEmpty();
        notification.ExtractionSummary.Should().BeNull();
    }
}

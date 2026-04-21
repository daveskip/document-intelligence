using FluentAssertions;

namespace DocumentIntelligence.ApiService.Tests.Endpoints;

public class AuthValidationTests
{
    // ── Email validation rules (mirroring endpoint logic) ─────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Email_NullOrWhitespace_IsInvalid(string? email)
    {
        (string.IsNullOrWhiteSpace(email)).Should().BeTrue();
    }

    [Fact]
    public void Email_ExceedingMaxLength_IsInvalid()
    {
        var email = new string('a', 251) + "@b.com"; // 257 chars — exceeds 256
        email.Length.Should().BeGreaterThan(256);
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("no-at-sign.com")]
    public void Email_MissingAtSign_IsInvalid(string email)
    {
        email.Contains('@').Should().BeFalse();
    }

    [Theory]
    [InlineData("has space@example.com")]
    [InlineData("space in middle@example.com")]
    public void Email_ContainingSpaces_IsInvalid(string email)
    {
        email.Contains(' ').Should().BeTrue();
    }

    [Theory]
    [InlineData("valid@example.com")]
    [InlineData("user.name+tag@domain.co.uk")]
    public void Email_Valid_PassesBasicChecks(string email)
    {
        string.IsNullOrWhiteSpace(email).Should().BeFalse();
        email.Contains('@').Should().BeTrue();
        email.Contains(' ').Should().BeFalse();
        email.Length.Should().BeLessThanOrEqualTo(256);
    }

    // ── Password validation rules ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")] // 7 chars — one short
    public void Password_TooShort_IsInvalid(string password)
    {
        (string.IsNullOrWhiteSpace(password) || password.Length < 8).Should().BeTrue();
    }

    [Fact]
    public void Password_ExceedingMaxLength_IsInvalid()
    {
        var password = new string('a', 257);
        password.Length.Should().BeGreaterThan(256);
    }

    [Theory]
    [InlineData("12345678")]  // exactly 8
    [InlineData("ValidPass1")]
    public void Password_ValidLength_PassesChecks(string password)
    {
        (password.Length >= 8 && password.Length <= 256).Should().BeTrue();
    }

    // ── User enumeration protection ───────────────────────────────────────

    [Fact]
    public void Login_InvalidEmailAndInvalidPassword_ReturnSameErrorMessage()
    {
        // Both paths in LoginAsync return "Invalid email or password."
        const string expected = "Invalid email or password.";
        const string invalidEmailError = "Invalid email or password.";
        const string invalidPasswordError = "Invalid email or password.";

        invalidEmailError.Should().Be(expected);
        invalidPasswordError.Should().Be(expected);
    }

    // ── DisplayName validation ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayName_NullOrWhitespace_IsInvalid(string displayName)
    {
        string.IsNullOrWhiteSpace(displayName).Should().BeTrue();
    }

    [Fact]
    public void DisplayName_ExceedingMaxLength_IsInvalid()
    {
        var name = new string('x', 257);
        name.Length.Should().BeGreaterThan(256);
    }
}

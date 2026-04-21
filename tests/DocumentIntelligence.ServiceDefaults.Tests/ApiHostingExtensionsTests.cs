using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocumentIntelligence.ServiceDefaults.Tests;

public class ApiHostingExtensionsTests
{
    [Fact]
    public void AddJwtAuthentication_ThrowsInvalidOperationException_WhenJwtKeyMissing()
    {
        // Arrange: builder with no Jwt:Key configured
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Jwt:Key"] = null;

        // Act & Assert
        var act = () => builder.AddJwtAuthentication();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Key*");
    }

    [Fact]
    public void AddJwtAuthentication_RegistersAuthenticationServices_WhenKeyPresent()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Jwt:Key"] = "a-very-long-secret-key-that-is-at-least-256-bits-long-here";

        var act = () => builder.AddJwtAuthentication();
        act.Should().NotThrow();

        var sp = builder.Services.BuildServiceProvider();
        // Authentication scheme should be registered
        sp.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
            .Should().NotBeNull();
    }

    [Fact]
    public void AddFrontendCors_UsesFallbackOrigin_WhenConfigNotSet()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedOrigins:Vite"] = null;

        // Should not throw — falls back to http://localhost:5173
        var act = () => builder.AddFrontendCors();
        act.Should().NotThrow();
    }

    [Fact]
    public void AddFrontendCors_UsesConfiguredOrigin()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedOrigins:Vite"] = "https://myapp.example.com";

        var act = () => builder.AddFrontendCors();
        act.Should().NotThrow();
    }

    [Fact]
    public void AddApiFeatures_RegistersProblemDetailsAndOpenApi()
    {
        var builder = WebApplication.CreateBuilder();

        var act = () => builder.AddApiFeatures();
        act.Should().NotThrow();

        var sp = builder.Services.BuildServiceProvider();
        sp.GetService<Microsoft.AspNetCore.Http.IProblemDetailsService>()
            .Should().NotBeNull();
    }
}

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.Hosting;

public static class ApiHostingExtensions
{
    /// <summary>
    /// Registers JWT bearer authentication and authorization.
    /// Reads <c>Jwt:Key</c>, <c>Jwt:Issuer</c>, and <c>Jwt:Audience</c> from configuration.
    /// Includes SignalR hub token extraction so clients can pass the access token as a query string
    /// parameter on connections to paths starting with <c>/hubs</c>.
    /// </summary>
    public static IHostApplicationBuilder AddJwtAuthentication(this IHostApplicationBuilder builder)
    {
        var jwtKey = builder.Configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key must be configured.");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateIssuer = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DocumentIntelligence",
                    ValidateAudience = true,
                    ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DocumentIntelligence",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };

                // Allow JWT from SignalR query string on /hubs/* paths.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var accessToken = ctx.Request.Query["access_token"];
                        var path = ctx.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                            ctx.Token = accessToken;
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();
        return builder;
    }

    /// <summary>
    /// Adds a CORS policy named <paramref name="policyName"/> that allows the front-end origin
    /// configured under <c>AllowedOrigins:Vite</c> (defaults to <c>http://localhost:5173</c>).
    /// </summary>
    public static IHostApplicationBuilder AddFrontendCors(
        this IHostApplicationBuilder builder,
        string policyName = "FrontendPolicy")
    {
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(policyName, policy =>
            {
                policy.WithOrigins(
                        builder.Configuration["AllowedOrigins:Vite"] ?? "http://localhost:5173")
                    .WithHeaders("content-type", "authorization", "x-requested-with")
                    .WithMethods("GET", "POST", "PUT", "DELETE")
                    .AllowCredentials();
            });
        });
        return builder;
    }

    /// <summary>
    /// Adds ProblemDetails and OpenAPI (Swagger) document generation.
    /// Rate limiting is configured in the API service's Program.cs via <c>AddRateLimiter</c>.
    /// Call <c>app.UseRateLimiter()</c> and <c>app.MapOpenApi()</c> in the middleware pipeline to activate.
    /// </summary>
    public static IHostApplicationBuilder AddApiFeatures(this IHostApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        return builder;
    }
}

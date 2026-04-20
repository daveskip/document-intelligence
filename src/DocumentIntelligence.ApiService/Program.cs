using System.Threading.RateLimiting;
using DocumentIntelligence.ApiService.Endpoints;
using DocumentIntelligence.ApiService.Hubs;
using DocumentIntelligence.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructure();

// ── Auth, CORS, API features (defined in ServiceDefaults) ──────────────────
builder.AddJwtAuthentication();
builder.Services.AddSignalR();
builder.AddFrontendCors();
builder.AddApiFeatures();

// ── Rate limiting ──────────────────────────────────────────────────────────
// Auth: fixed window per IP — limits brute-force / credential stuffing.
// Upload: sliding window per IP — limits excessive document uploads.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("upload", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromHours(1),
                SegmentsPerWindow = 12,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

// ── Startup configuration validation ──────────────────────────────────────
if (string.IsNullOrEmpty(app.Configuration["Internal:SharedKey"]))
    throw new InvalidOperationException("Internal:SharedKey must be configured before starting the API.");

// ── Apply EF Migrations ────────────────────────────────────────────────────
if (app.Configuration.GetValue("ApplyMigrationsOnStartup", defaultValue: true))
{
    await app.Services.ApplyMigrationsAsync();
}

// ── Middleware pipeline ────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseCors("FrontendPolicy");
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────
app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapDocumentEndpoints();
app.MapInternalEndpoints();
app.MapHub<DocumentStatusHub>("/hubs/documents");

app.Run();

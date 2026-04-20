using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DocumentIntelligence.Infrastructure.Data;
using DocumentIntelligence.Infrastructure.Identity;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DocumentIntelligence.Infrastructure;

public static class InfrastructureExtensions
{
    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        // EF Core + PostgreSQL via Aspire
        builder.AddNpgsqlDbContext<AppDbContext>("documentintelligence", configureDbContextOptions: options =>
        {
            options.UseNpgsql(o => o.MigrationsAssembly(typeof(InfrastructureExtensions).Assembly.FullName));
        });

        // Azure Storage via Aspire
        builder.AddAzureBlobServiceClient("document-blobs");

        // Azure Service Bus via Aspire
        builder.AddAzureServiceBusClient("servicebus");

        // Identity
        builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        builder.Services.AddDataAccessServices();
        builder.Services.AddSingleton<IDocumentQueuePublisher, DocumentQueuePublisher>();

        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["db", "ready"])
            .AddCheck<BlobStorageHealthCheck>("blob-storage", tags: ["storage", "ready"]);

        return builder;
    }

    /// <summary>
    /// Registers infrastructure services for non-Aspire hosts (e.g., Azure Functions isolated worker).
    /// Reads connection strings from configuration directly rather than Aspire service bindings.
    /// Note: <see cref="IDocumentQueuePublisher"/> is intentionally omitted — Functions consume
    /// from Service Bus via trigger binding and do not need to publish.
    /// </summary>
    public static IServiceCollection AddInfrastructureForFunctions(
        this IServiceCollection services, IConfiguration config)
    {
        var pgConn = config["ConnectionStrings:documentintelligence"]
            ?? throw new InvalidOperationException("ConnectionStrings:documentintelligence not configured.");
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(pgConn));

        // When running locally via AddAzureFunctionsProject, Aspire injects the storage account
        // connection string as AzureWebJobsStorage (via WithHostStorage). The blob sub-resource
        // may not expose its own ConnectionStrings entry, so fall back to AzureWebJobsStorage.
        var blobConn = config["ConnectionStrings:document-blobs"]
            ?? config["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("ConnectionStrings:document-blobs not configured.");
        services.AddSingleton(_ => new BlobServiceClient(blobConn));

        services.AddDataAccessServices();

        return services;
    }

    public static async Task ApplyMigrationsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    private static IServiceCollection AddDataAccessServices(this IServiceCollection services)
    {
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IBlobStorageService, BlobStorageService>();
        return services;
    }
}

/// <summary>
/// Checks connectivity to the PostgreSQL database via the EF Core context.
/// Uses a new scope so the scoped DbContext is not captured in a singleton.
/// </summary>
file sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Unable to connect to the database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

/// <summary>
/// Checks connectivity to Azure Blob Storage by requesting service properties.
/// </summary>
file sealed class BlobStorageHealthCheck(BlobServiceClient blobClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await blobClient.GetPropertiesAsync(cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

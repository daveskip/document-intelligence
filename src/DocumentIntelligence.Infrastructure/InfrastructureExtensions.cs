using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DocumentIntelligence.Infrastructure.Data;
using DocumentIntelligence.Infrastructure.Identity;
using DocumentIntelligence.Infrastructure.Repositories;
using DocumentIntelligence.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        // Repositories & Services
        builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
        builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
        builder.Services.AddScoped<IDocumentQueuePublisher, DocumentQueuePublisher>();

        return builder;
    }

    public static async Task ApplyMigrationsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }
}

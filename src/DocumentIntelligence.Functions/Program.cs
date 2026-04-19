using Azure.Storage.Blobs;
using DocumentIntelligence.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OllamaSharp;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // PostgreSQL via Aspire-injected connection string
        var pgConn = context.Configuration["ConnectionStrings:documentintelligence"]
            ?? throw new InvalidOperationException("ConnectionStrings:documentintelligence not found.");
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(pgConn));

        // Azure Blob Storage via Aspire-injected connection string
        var blobConn = context.Configuration["ConnectionStrings:document-blobs"]
            ?? throw new InvalidOperationException("ConnectionStrings:document-blobs not found.");
        services.AddSingleton(_ => new BlobServiceClient(blobConn));

        // Ollama via Aspire-injected service endpoint
        var ollamaUrl = context.Configuration["services:ollama:http:0"]
            ?? context.Configuration["services:ollama:https:0"]
            ?? context.Configuration.GetConnectionString("ollama")
            ?? throw new InvalidOperationException("Ollama endpoint not found in configuration.");
        services.AddSingleton(new OllamaApiClient(
            new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(10) }));

        // HttpClient for calling back to ApiService (Aspire service discovery URL)
        services.AddHttpClient("apiservice", (sp, client) =>
        {
            var baseUrl = context.Configuration["services:apiservice:https:0"]
                ?? context.Configuration["services:apiservice:http:0"]
                ?? "http://localhost:5000";
            client.BaseAddress = new Uri(baseUrl);
        });
    })
    .Build();

host.Run();

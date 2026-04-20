using DocumentIntelligence.Functions.Services;
using DocumentIntelligence.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OllamaSharp;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // DbContext, BlobServiceClient, IDocumentRepository, IBlobStorageService
        services.AddInfrastructureForFunctions(context.Configuration);

        // Ollama via Aspire-injected service endpoint
        var ollamaUrl = context.Configuration["services:ollama:http:0"]
            ?? context.Configuration["services:ollama:https:0"]
            ?? context.Configuration.GetConnectionString("ollama")
            ?? throw new InvalidOperationException("Ollama endpoint not found in configuration.");
        services.AddSingleton(new OllamaApiClient(
            new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(10) }));

        // Document extraction: AI prompting + content parsing
        services.AddScoped<IDocumentExtractionService, DocumentExtractionService>();

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

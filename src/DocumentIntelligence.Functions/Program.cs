using DocumentIntelligence.Functions.Services;
using DocumentIntelligence.Infrastructure;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OllamaSharp;

var builder = FunctionsApplication.CreateBuilder(args);

// DbContext, BlobServiceClient, IDocumentRepository, IBlobStorageService
builder.Services.AddInfrastructureForFunctions(builder.Configuration);

// Ollama via Aspire-injected service endpoint.
// AddAzureFunctionsProject injects WithReference(ollama) as a connection string
// in the format "Endpoint=http://localhost:11434", so parse the Endpoint value if present.
var ollamaRaw = builder.Configuration["services:ollama:http:0"]
    ?? builder.Configuration["services:ollama:https:0"]
    ?? builder.Configuration.GetConnectionString("ollama")
    ?? throw new InvalidOperationException("Ollama endpoint not found in configuration.");
var ollamaUrl = ollamaRaw.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)
    ? ollamaRaw["Endpoint=".Length..]
    : ollamaRaw;
builder.Services.AddSingleton(new OllamaApiClient(
    new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(10) }));

// Document extraction: AI prompting + content parsing
builder.Services.AddScoped<IDocumentExtractionService, DocumentExtractionService>();

// HttpClient for calling back to ApiService (Aspire service discovery URL)
builder.Services.AddHttpClient("apiservice", client =>
{
    var baseUrl = builder.Configuration["services:apiservice:https:0"]
        ?? builder.Configuration["services:apiservice:http:0"]
        ?? "http://localhost:5000";
    client.BaseAddress = new Uri(baseUrl);
});

builder.Build().Run();

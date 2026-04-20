using CommunityToolkit.Aspire.Hosting.Ollama;

var builder = DistributedApplication.CreateBuilder(args);

// ── PostgreSQL ─────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .WithDataVolume("docint-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var db = postgres.AddDatabase("documentintelligence");

// ── Azure Storage (Azurite) ────────────────────────────────────────────────
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(c =>
        c.WithDataVolume("docint-azurite-data")
        .WithLifetime(ContainerLifetime.Persistent));

var blobs = storage.AddBlobs("document-blobs");

// ── Azure Service Bus Emulator ─────────────────────────────────────────────
var serviceBus = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator(c =>
        c.WithConfigurationFile("./servicebus-config.json")
        .WithLifetime(ContainerLifetime.Persistent));

serviceBus.AddServiceBusQueue("document-processing", "document-processing");

// ── Service-to-service shared key ───────────────────────────────────────────
// Generated fresh each AppHost start in dev. In production provide via Key Vault / env.
var internalSharedKey = $"{Guid.NewGuid()}-{Guid.NewGuid()}";

// ── Ollama (Gemma 4) ───────────────────────────────────────────────────────
var ollama = builder.AddOllama("ollama", port: 11434)
    .WithImageTag("latest")
    .WithDataVolume("docint-ollama-data")
    .WithGPUSupport()
    .AddModel("gemma4:e4b");

// ── API Service ────────────────────────────────────────────────────────────
var apiService = builder.AddProject<Projects.DocumentIntelligence_ApiService>("apiservice")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Internal__SharedKey", internalSharedKey)
    .WithReference(db)
    .WithReference(blobs)
    .WithReference(serviceBus)
    .WaitFor(db)
    .WaitFor(blobs)
    .WaitFor(serviceBus);

// ── Azure Functions Processor ──────────────────────────────────────────────
builder.AddDockerfile("functions", "../..", "src/DocumentIntelligence.Functions/Dockerfile")
    .WithHttpEndpoint(targetPort: 80)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("AZURE_FUNCTIONS_ENVIRONMENT", "Development")
    .WithEnvironment("FUNCTIONS_WORKER_RUNTIME", "dotnet-isolated")
    .WithEnvironment("AzureFunctionsJobHost__Logging__LogLevel__Default", "Information")
    .WithEnvironment("AzureFunctionsJobHost__Logging__LogLevel__Host", "Warning")
    .WithEnvironment("AzureWebJobsStorage", blobs.Resource.ConnectionStringExpression)
    .WithEnvironment("servicebus", serviceBus.Resource.ConnectionStringExpression)
    .WithReference(db)
    .WithReference(blobs)
    .WithReference(serviceBus)
    .WithEnvironment("services__ollama__http__0", "http://host.docker.internal:11434")
    .WithEnvironment("Internal__SharedKey", internalSharedKey)
    .WithReference(apiService)
    .WaitFor(apiService)
    .WaitFor(serviceBus)
    .WaitFor(ollama);

// ── React Frontend (Vite dev server) ──────────────────────────────────────
builder.AddViteApp("web", "../../src/DocumentIntelligence.Web")
    .WithHttpsEndpoint(port: 5300, env: "VITE_PORT")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

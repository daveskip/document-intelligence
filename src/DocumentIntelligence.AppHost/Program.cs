using CommunityToolkit.Aspire.Hosting.Ollama;
using Microsoft.Extensions.Configuration;

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

// ── Ollama ────────────────────────────────────────────────────────────────
// OllamaNetworkIsolation (appsettings.json or env var) controls whether the
// container is prevented from making outbound internet connections. When true,
// the container's DNS resolver is pointed at a non-existent address so external
// hostnames cannot be resolved — document content cannot leave the host machine.
//
// FIRST-TIME SETUP: leave OllamaNetworkIsolation=false (the default), run once
// so the model is downloaded into the docint-ollama-data volume, then set it to
// true. The volume persists across restarts, so re-pulling is not needed again
// unless you intentionally want to upgrade the model.
var ollamaIsolated = builder.Configuration.GetValue("OllamaNetworkIsolation", defaultValue: false);

var ollama = builder.AddOllama("ollama", port: 11434)
    .WithImageTag("latest")
    .WithDataVolume("docint-ollama-data")
    .WithGPUSupport();

if (ollamaIsolated)
{
    // Route all DNS queries inside the container to loopback (nothing is listening
    // there), blocking any outbound connection that requires hostname resolution.
    // Direct IP connections within Docker's internal bridge still work, so the
    // Functions service can reach Ollama on its internal IP without DNS.
    ollama.WithContainerRuntimeArgs("--dns", "127.0.0.1");
}
else
{
    // Only register the model pull when isolation is off — the pull contacts
    // registry.ollama.ai, which is unreachable when DNS is blocked.
    // Previously used "gemma4:e4b".
    ollama.AddModel("qwen2.5vl:7b");
}

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

// ── Azure Functions Processor (runs locally for easier debugging) ─────────
builder.AddAzureFunctionsProject<Projects.DocumentIntelligence_Functions>("functions")
    .WithHostStorage(storage)
    .WithEnvironment("Internal__SharedKey", internalSharedKey)
    .WithReference(db)
    .WithReference(blobs)
    .WithReference(serviceBus)
    .WithReference(ollama)
    .WithReference(apiService)
    .WaitFor(apiService)
    .WaitFor(serviceBus);

// ── React Frontend (Vite dev server) ──────────────────────────────────────
builder.AddViteApp("web", "../../src/DocumentIntelligence.Web")
    .WithHttpsEndpoint(port: 5300, env: "VITE_PORT")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

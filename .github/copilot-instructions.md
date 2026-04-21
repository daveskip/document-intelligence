# Document Intelligence — Copilot & Claude Instructions

## Solution Overview

Eight projects with strict layering. Never let lower layers reference higher ones.

```
DocumentIntelligence.Web          (React 19 / TypeScript 6 / Vite — frontend)
DocumentIntelligence.AppHost      (Aspire orchestrator — dev-only, not deployed)
DocumentIntelligence.ApiService   (ASP.NET Core 10 Minimal API — HTTP host)
DocumentIntelligence.Functions    (Azure Functions isolated worker — Service Bus consumer)
DocumentIntelligence.ServiceDefaults  (shared startup helpers — JWT, CORS, OTel, health)
DocumentIntelligence.Infrastructure  (EF Core, repos, blob, queue, health checks)
DocumentIntelligence.Domain       (entities + enums — no framework deps)
DocumentIntelligence.Contracts    (DTOs, messages, requests — no framework deps)
```

**Allowed references:** ApiService and Functions depend on Infrastructure and ServiceDefaults. Infrastructure depends on Domain and Contracts. Contracts depends on Domain. Domain has no project dependencies.

## Build & Run

```bash
# Start everything (Postgres, Azurite, Service Bus emulator, Ollama, API, Functions, Web)
dotnet run --project src/DocumentIntelligence.AppHost

# Build entire solution
dotnet build DocumentIntelligence.slnx

# Run EF migrations manually (normally auto-applied on startup)
dotnet ef database update --project src/DocumentIntelligence.Infrastructure --startup-project src/DocumentIntelligence.ApiService
```

**Frontend** runs on `https://localhost:5300` (Vite dev server proxied through Aspire).  
**API** runs on `http://localhost:5000` (visible in Aspire dashboard).

## C# Conventions

### File structure
- File-scoped namespaces (`namespace DocumentIntelligence.ApiService.Endpoints;`)
- One type per file as the default; `file sealed class` for private implementation types within the same file (see health check classes in `InfrastructureExtensions.cs`)
- No global usings; explicit `using` directives at the top of each file, System namespaces first

### Types
- **Entities** (`Domain/Entities/`): `class` with settable properties, no constructor args
- **DTOs, requests, messages** (`Contracts/`): positional `record` — immutable, one line when short
- **Services, repositories**: `class` with primary constructor injection
- **Configuration guards**: `?? throw new InvalidOperationException(...)` at the injection site, never silently default a required config value

```csharp
// ✅ Correct — record for DTO
public record DocumentDto(Guid Id, string FileName, DocumentStatus Status, DateTimeOffset UploadedAt);

// ✅ Correct — primary constructor for service
public class BlobStorageService(BlobServiceClient blobClient, ILogger<BlobStorageService> logger) : IBlobStorageService { ... }

// ❌ Wrong — class for DTO, explicit constructor for service
```

### Naming
| Thing | Convention | Example |
|-------|-----------|---------|
| Endpoint files | `*Endpoints.cs` | `DocumentEndpoints.cs` |
| Service interfaces | `I*Service` / `I*Repository` | `IBlobStorageService` |
| Hub classes | `*Hub.cs` | `DocumentStatusHub.cs` |
| Namespace | `DocumentIntelligence.[Project].[Folder]` | `DocumentIntelligence.ApiService.Endpoints` |
| Private async methods | verb + noun + Async | `RunOllamaInferenceAsync` |

### Nullable
- `<Nullable>enable</Nullable>` in every project — never disable
- Prefer `is null` / `is not null` over `== null`
- Use `!` (null-forgiving) only when the compiler cannot prove non-null but you have a contextual guarantee (e.g., after an auth guard)

## API Endpoint Patterns

All endpoints use ASP.NET Core Minimal API. Every route group follows this template:

```csharp
public static IEndpointRouteBuilder MapXxxEndpoints(this IEndpointRouteBuilder app)
{
    var group = app.MapGroup("/api/v1/xxx")
        .WithTags("Xxx")
        .RequireAuthorization();              // omit for auth group; add .AllowAnonymous() per route

    group.MapGet("/", GetXxxAsync)
        .WithName("GetXxx")
        .WithSummary("One-line description.")
        .Produces<XxxDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

    return app;
}
```

**Rules:**
- All routes are prefixed `/api/v1/`
- Auth routes use `.RequireRateLimiting("auth")` on the group
- The upload endpoint uses `.RequireRateLimiting("upload")` per-route
- Internal service-to-service endpoints go in `InternalEndpoints.cs`, are excluded from OpenAPI with `.ExcludeFromDescription()`, and authenticate via `X-Internal-Key` header
- Every route must declare `.WithName()`, `.WithSummary()`, and full `.Produces<T>()` / `.ProducesProblem()` coverage
- Endpoint handler methods are `private static async Task<IResult>`

### Error responses
```csharp
Results.ValidationProblem(errors)            // 400 — dict of field → string[]
Results.BadRequest("message")                // 400 — plain message
Results.Problem("message", statusCode: 401)  // non-2xx with ProblemDetails
Results.Unauthorized()                       // 401
Results.Forbid()                             // 403
Results.NotFound()                           // 404
Results.NotFound("specific message")         // 404 with body
```

Never silently clamp invalid parameters. Return `400 Bad Request` if page/pageSize are out of range.

## Security Rules

These patterns are non-negotiable. Do not weaken them.

### Sensitive config
- `Jwt:Key` — user-secrets in dev, Key Vault in production. Never commit to any `appsettings*.json`.
- `Internal:SharedKey` — generated by AppHost at startup (`$"{Guid.NewGuid()}-{Guid.NewGuid()}"`) and injected into both ApiService and Functions as `Internal__SharedKey` env var. Do not hardcode.

### Authentication
- JWT HS256 with 1-hour expiry and 1-minute clock skew
- Refresh tokens: raw token is `Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))`, stored in DB as `Convert.ToHexString(SHA256.HashData(...))`. The client always receives the raw token; only the hash is persisted.
- Token lookup: `FirstOrDefaultAsync(u => u.RefreshToken == hashedValue)` — never do a LINQ `.Where().FirstOrDefault()` without `Async`

### Timing-safe comparisons
Use `CryptographicOperations.FixedTimeEquals` whenever comparing secrets (internal key, tokens). Never use `==` or `string.Compare` for security-sensitive string equality.

### Authorization in SignalR hub
`JoinDocumentGroup` must verify the document belongs to the authenticated user via `IDocumentRepository` before calling `AddToGroupAsync`. Throw `HubException("Document not found or access denied.")` on failure.

### Input validation
- Filenames: always sanitize with `Path.GetFileName()` and reject names containing `".."`
- Email: check for `@`, no spaces, max 256 chars — before any DB call
- Password: 8–256 chars — before any DB call
- Return the same error message for invalid email vs invalid password to prevent user enumeration

### Error messages to clients
Never expose `ex.Message` in client-facing responses. Use a generic message (`"Processing failed. Please try again or contact support."`) and log the full exception server-side with `logger.LogError(ex, ...)`.

### Logging and PII
- Never log extracted document content, field values, user-submitted text, or passwords
- Log field names (keys) but not values from AI extraction results
- Use `logger.LogDebug` for prompt template structure; `logger.LogInformation` for field names + response char count

## Infrastructure Patterns

### DI registration
- **Aspire host (ApiService):** use `builder.AddNpgsqlDbContext<AppDbContext>(...)`, `builder.AddAzureBlobServiceClient(...)`, `builder.AddAzureServiceBusClient(...)` — these use Aspire service discovery
- **Non-Aspire host (Functions):** use `AddInfrastructureForFunctions(services, config)` — reads raw connection strings from `IConfiguration`
- `IDocumentQueuePublisher` is `AddSingleton` (depends only on singleton `ServiceBusClient`)
- `IDocumentRepository` and `IBlobStorageService` are `AddScoped`

### Repository pattern
Every repository method accepts a `CancellationToken` as the last parameter. Use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` for single-row writes that don't need entity tracking. Call `SaveChangesAsync` explicitly — do not auto-save on `AddAsync`.

### Health checks
Database and blob storage health checks are registered in `AddInfrastructure()`. New infrastructure dependencies must add a corresponding `IHealthCheck` implementation as a `file sealed class` at the bottom of `InfrastructureExtensions.cs`.

### Blob storage
- Container: `"documents"`, access: `PublicAccessType.None`
- Upload path: `$"{Guid.NewGuid():N}/{fileName}"` — always prefix with a UUID to prevent collisions and path guessing

### Auto-migrations
Controlled by `ApplyMigrationsOnStartup` config flag (defaults `true`). Do not run migrations unconditionally.

## Azure Functions Patterns

- Isolated worker model — do not reference `Microsoft.Azure.WebJobs` directly
- Service Bus trigger uses `Connection = "servicebus"` (Aspire injects the connection string as `servicebus`)
- Deserialization errors: log with a body preview, then `return` (abandon for dead-lettering) — do not re-throw
- Processing errors: log with `LogError`, use a generic client message, then re-throw so Service Bus dead-letters after max delivery count
- `IDocumentExtractionService` must be `AddScoped`; `OllamaApiClient` is `AddSingleton`
- Ollama calls: always use a linked `CancellationTokenSource` with a 10-minute `CancelAfter` timeout

## AppHost Orchestration

- Never reference concrete service URLs — use Aspire `WithReference()` and `WaitFor()`
- Shared secrets (e.g., `internalSharedKey`) must be generated in AppHost and injected via `WithEnvironment("Key__SubKey", value)` (double-underscore = colon in config)
- Container data volumes use the `docint-` prefix: `"docint-postgres-data"`, `"docint-ollama-data"`, etc.
- Functions are added via `builder.AddDockerfile(...)`, not as a project reference, because they run in a container

## Frontend Patterns

### API client
All HTTP calls go through `src/lib/api.ts`. The axios base URL is `/api/v1`. Never construct fetch/axios calls with hardcoded base URLs in pages or hooks.

```ts
// ✅ Correct
const { data } = await api.get<DocumentDto[]>('/documents')

// ❌ Wrong — bypasses interceptors and base URL
const { data } = await axios.get('http://localhost:5000/api/v1/documents')
```

### Data fetching
Use TanStack Query (`useQuery`, `useMutation`) for all server state. Do not use `useEffect` + `useState` for data fetching.

```ts
const { data, isLoading, error } = useQuery({
  queryKey: ['documents', page],
  queryFn: () => api.get<PagedResult<DocumentDto>>(`/documents?page=${page}&pageSize=${PAGE_SIZE}`).then(r => r.data),
})
```

### Authentication
Access token in `sessionStorage`, refresh token in `localStorage`. The axios interceptor in `api.ts` handles 401 → refresh → retry automatically. Pages and hooks should never manually manage tokens.

### SignalR
Use the existing `useDocumentSignalR(documentId)` / `useDashboardSignalR()` hooks. Always call `LeaveDocumentGroup` before disconnecting. Access token is provided via `accessTokenFactory` in the connection builder, not as a query param in application code.

### TypeScript
- Prefer `interface` for object types in `types/api.ts`
- Strict mode is enabled — all unused variables are errors
- `DocumentStatus` is the union type `'Pending' | 'Processing' | 'Completed' | 'Failed'`
- New API response shapes go in `src/types/api.ts` (manually for now; `npm run generate-api` can regenerate from the OpenAPI schema when the API is running)

### Folder structure
| Folder | Contents |
|--------|----------|
| `src/components/` | Reusable UI components (`Layout`, `StatusBadge`) |
| `src/context/` | React context providers (`AuthContext`) |
| `src/hooks/` | Custom hooks (`useDocumentSignalR`) |
| `src/lib/` | Utilities (`api.ts`) |
| `src/pages/` | Route-level components |
| `src/types/` | TypeScript type definitions |

## Contracts Layer Rules

`DocumentIntelligence.Contracts` has no ASP.NET or EF references. Keep it that way. Every type in Contracts is a `record`. Both the API and Functions reference this project — any breaking change to a record requires updating both.

## Unit Testing Rules

Always implement unit tests alongside every code change. Do not defer tests to a follow-up task.

- **Test project per source project**: `tests/DocumentIntelligence.<Project>.Tests/`
- **Framework**: xUnit + FluentAssertions + NSubstitute (already referenced in every test project)
- **File naming**: match the file under test — `DocumentProcessingFunction.cs` → `DocumentProcessingFunctionTests.cs`
- **SQLite in-memory** (`SqliteAppDbContext`) for any test that exercises EF Core — never use the real Postgres provider in tests
- **Mock all I/O** (repositories, blob storage, HTTP clients, queues) with `NSubstitute.Substitute.For<T>()`
- Private static helper methods (e.g. `ComputeConfidenceScore`) must be tested indirectly through the public surface (`RunAsync`) by asserting on the stored DB state or return value
- Organise tests with `// ── Section name ──` region comments, matching the pattern used in the existing test files
- Every new endpoint handler must have tests covering: happy path, ownership/auth guard (wrong user), not-found, and all business-rule rejections (status guard, validation errors)
- Every new pure-logic method must have tests for: all-valid input, boundary values, null/empty input, and parse-failure fallback

## Adding a New Feature Checklist

### New API endpoint
1. Add handler in the appropriate `*Endpoints.cs` or create a new `*Endpoints.cs`
2. Register with `MapGroup("/api/v1/...")` and declare all `.Produces<T>()` metadata
3. Add request/response types to `Contracts/Requests/` or `Contracts/Responses/` as records
4. Add interface + implementation to `Infrastructure` if new infrastructure is needed
5. Register new services in `InfrastructureExtensions.cs`
6. Call `MapXxxEndpoints()` from `ApiService/Program.cs`
7. **Add unit tests** covering happy path, ownership guard, not-found, and all validation rejections

### New domain entity
1. Add entity `class` to `Domain/Entities/`
2. Add `DbSet<T>` to `AppDbContext` and configure in `OnModelCreating`
3. Add `IRepository` interface + implementation in `Infrastructure/Repositories/`
4. Register in `AddDataAccessServices()`
5. Add EF migration: `dotnet ef migrations add <Name> --project src/DocumentIntelligence.Infrastructure --startup-project src/DocumentIntelligence.ApiService`

### New config value
- **Required:** validate at startup with `?? throw new InvalidOperationException("...")` — never defer validation
- **Secret:** add to user-secrets for dev, document that production requires Key Vault / environment variable
- **Shared between ApiService and Functions:** inject from AppHost via `WithEnvironment("Key__SubKey", value)` using double-underscore notation

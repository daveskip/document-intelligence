# Document Intelligence

An end-to-end document processing platform that uses a local AI vision model to extract structured data from uploaded documents. Users upload PDFs or images; the system processes them asynchronously and presents the extracted fields and tables in an interactive UI — all without sending data to external AI services.

## What it does

1. **Upload** — users authenticate and upload PDFs, JPEG, PNG, or TIFF files (up to 50 MB).
2. **Queue** — the API publishes a message to an Azure Service Bus queue and immediately returns a `Pending` document record.
3. **Extract** — an Azure Functions worker picks up the message, downloads the file from blob storage, rasterises PDF pages if needed, and runs vision inference via a locally-hosted **Qwen2.5-VL 7B** model through Ollama.
4. **Notify** — the function calls an internal API endpoint which pushes a real-time status update to the browser via **SignalR**.
5. **View** — the React frontend displays extracted key-value fields and tables, with sortable columns, currency formatting, and totals footers.

## Architecture

```
DocumentIntelligence.Web           React 19 + TypeScript + Vite (frontend)
DocumentIntelligence.AppHost       .NET Aspire orchestrator (dev only)
DocumentIntelligence.ApiService    ASP.NET Core 10 Minimal API
DocumentIntelligence.Functions     Azure Functions isolated worker (Service Bus consumer)
DocumentIntelligence.ServiceDefaults  Shared startup helpers (JWT, CORS, OTel, health checks)
DocumentIntelligence.Infrastructure   EF Core, repositories, blob storage, queue publisher
DocumentIntelligence.Domain        Entities and enums (no framework dependencies)
DocumentIntelligence.Contracts     DTOs, messages, requests (no framework dependencies)
```

Dependency flow is strictly top-down. Domain and Contracts have no project references; Infrastructure depends on both; ApiService and Functions depend on Infrastructure and ServiceDefaults; the Web frontend is completely separate.

## Tech stack

| Layer | Technology |
|---|---|
| Frontend | React 19, TypeScript, Vite, Tailwind CSS 4, TanStack Query, Axios, SignalR JS client |
| API | ASP.NET Core 10 Minimal API, SignalR, JWT HS256, rate limiting |
| Background processor | Azure Functions (isolated worker model) |
| AI inference | Ollama + Qwen2.5-VL 7B (local, runs in Docker) |
| Database | PostgreSQL via EF Core 10, ASP.NET Core Identity |
| Storage | Azure Blob Storage (Azurite emulator in dev) |
| Messaging | Azure Service Bus (emulator in dev) |
| Orchestration | .NET Aspire 13 |
| Observability | OpenTelemetry (traces, metrics, logs) via Aspire dashboard |

## Getting started

### Prerequisites

Install the following tools before running the project for the first time.

#### Required

| Tool | Version | Notes |
|---|---|---|
| [.NET SDK](https://dot.net) | 10.0+ | Includes the `dotnet` CLI, EF tooling, and Aspire workload |
| [Node.js](https://nodejs.org) | 22 LTS+ | Includes `npm`; used to build and dev-serve the React frontend |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Latest | Must be running before `dotnet run`; hosts Postgres, Azurite, Service Bus emulator, and Ollama |

#### .NET workloads and global tools

After installing the .NET SDK, install the Aspire workload and the Azure Functions Core Tools:

```bash
# Aspire AppHost workload (required to run the orchestrator)
dotnet workload install aspire

# Azure Functions Core Tools v4 (required to run the Functions project locally)
npm install -g azure-functions-core-tools@4 --unsafe-perm true
```

> **Note:** The Functions project runs as a standard .NET process via Aspire in this repo — the Core Tools are used for local tooling support (debugging, `func` CLI commands) but the project does not require a separate `func start` invocation; Aspire starts it automatically.

#### EF Core CLI (optional, for manual migrations)

```bash
dotnet tool install --global dotnet-ef
```

#### GPU support (optional but recommended)

A CUDA-compatible GPU significantly speeds up Qwen2.5-VL inference. Docker Desktop must have GPU access enabled (**Settings → Resources → Advanced → Enable GPU**). The AppHost configures GPU passthrough automatically when available; the model will still run on CPU if no GPU is detected, but extraction will be much slower.

### Run everything with Aspire

```bash
# Install frontend dependencies
cd src/DocumentIntelligence.Web
npm install
cd ../..

# Start all services (Postgres, Azurite, Service Bus emulator, Ollama, API, Functions, Web)
dotnet run --project src/DocumentIntelligence.AppHost
```

The Aspire dashboard opens automatically and shows live logs, traces, and health status for every service. The first startup will pull the Qwen2.5-VL model into the Ollama container (~5 GB) — subsequent starts reuse the `docint-ollama-data` volume.

| Service | URL |
|---|---|
| React frontend | https://localhost:5300 |
| API | http://localhost:5000 |
| Aspire dashboard | http://localhost:15888 |
| pgAdmin | http://localhost:5050 |

### Development secrets

The JWT signing key must be set in user secrets (never committed to `appsettings*.json`):

```bash
dotnet user-secrets set "Jwt:Key" "your-256-bit-or-longer-secret" \
  --project src/DocumentIntelligence.ApiService
```

The internal service-to-service shared key (`Internal:SharedKey`) is generated fresh by AppHost on each start and injected automatically — no manual configuration needed in development.

### Build and test

```bash
# Build the entire solution
dotnet build DocumentIntelligence.slnx

# Run EF migrations manually (auto-applied on startup by default)
dotnet ef database update \
  --project src/DocumentIntelligence.Infrastructure \
  --startup-project src/DocumentIntelligence.ApiService

# Lint the frontend
cd src/DocumentIntelligence.Web
npm run lint

# Regenerate TypeScript API types from the live OpenAPI schema
npm run generate-api
```

## API overview

All routes are prefixed `/api/v1/`. The OpenAPI schema is available at `/openapi/v1.json` in Development.

| Method | Route | Description |
|---|---|---|
| `POST` | `/auth/register` | Create a new account |
| `POST` | `/auth/login` | Obtain access + refresh tokens |
| `POST` | `/auth/refresh` | Exchange refresh token for new tokens |
| `POST` | `/documents` | Upload a document (PDF/JPEG/PNG/TIFF, max 50 MB) |
| `GET` | `/documents` | Paged list of the user's documents |
| `GET` | `/documents/{id}` | Document detail |
| `GET` | `/documents/{id}/file` | Download the original file |
| `GET` | `/documents/{id}/results` | Structured AI extraction result |
| `DELETE` | `/documents/{id}` | Delete document and its blob |

Rate limits are enforced per IP: 10 auth requests/minute, 20 upload requests/hour.

Real-time status updates are delivered over SignalR at `/hubs/documents`.

## Security highlights

- JWT HS256 tokens with 1-hour expiry; refresh tokens stored as SHA-256 hashes only.
- Timing-safe comparisons (`CryptographicOperations.FixedTimeEquals`) for all secret equality checks.
- Blob paths are prefixed with a random UUID to prevent enumeration.
- Filenames are sanitised with `Path.GetFileName` and checked for path-traversal sequences.
- Register/login return identical error messages to prevent user enumeration.
- Internal service-to-service calls are authenticated via a shared key generated at runtime (never hardcoded).

## Project conventions

- File-scoped namespaces, explicit `using` directives (no global usings), nullable enabled everywhere.
- DTOs and messages are positional `record` types in `Contracts/`; entities are plain `class` types in `Domain/`.
- Services use primary constructor injection.
- Every repository method accepts a `CancellationToken`.
- Frontend data fetching uses TanStack Query; no `useEffect`+`useState` for server state.
- All HTTP calls go through `src/lib/api.ts` — never hardcoded base URLs in pages or hooks.

## Supported document types

The extraction prompt is tuned for financial and HR documents, including but not limited to:

- Tax forms: W-2, 1099-DIV, 1099-B, 1099-INT, 1099-MISC, 1040 and schedules
- Payroll summaries and pay stubs
- Employee records and HR documents
- Bank and brokerage statements
- Benefits enrollment forms

The model returns a JSON object with scalar fields, array tables, and a `_metadata` block (document type, page count, extraction notes). The frontend renders scalar fields as cards and tabular data as sortable, filterable tables with currency formatting and column totals.

## License

MIT

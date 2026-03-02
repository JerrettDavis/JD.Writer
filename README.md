# JD.Writer

[![CI](https://github.com/JerrettDavis/JD.Writer/actions/workflows/ci.yml/badge.svg)](https://github.com/JerrettDavis/JD.Writer/actions/workflows/ci.yml)
[![PR Validation](https://github.com/JerrettDavis/JD.Writer/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/JerrettDavis/JD.Writer/actions/workflows/pr-validation.yml)
[![Docs](https://github.com/JerrettDavis/JD.Writer/actions/workflows/docs.yml/badge.svg)](https://github.com/JerrettDavis/JD.Writer/actions/workflows/docs.yml)
[![Integration](https://github.com/JerrettDavis/JD.Writer/actions/workflows/integration.yml/badge.svg)](https://github.com/JerrettDavis/JD.Writer/actions/workflows/integration.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

JD.Writer is a local-first markdown studio for fast drafting, editing, and iteration.

It works in three ways:
- Full stack: web + API (best when you want Ollama/Semantic Kernel features)
- Standalone client-only: web app with deterministic local heuristics
- Static GitHub Pages app: in-browser Studio Lite with no server required
- Dedicated settings hub: persistent local-first configuration for theme/editor/AI/voice/plugins

## Why This Exists

Most notes apps make fast capture easy but structured iteration hard.
JD.Writer aims to give you both:
- quick typing and editing
- markdown-first output
- AI assist when available
- useful local fallback when AI is offline
- persisted voice transcription audit trails for review/QC

## Solution Layout

- `JD.Writer.Web`: Blazor studio UI
- `JD.Writer.ApiService`: AI endpoints (`/ai/continue`, `/ai/slash`, `/ai/assist/stream`)
- `JD.Writer.AppHost`: Aspire orchestration for local full-stack runs
- `JD.Writer.ServiceDefaults`: shared Aspire defaults
- `JD.Writer.E2E`: Reqnroll + Playwright acceptance tests

## Quick Start

### Prerequisites

- .NET SDK `10.0.103` (or compatible roll-forward from `global.json`)
- Optional: Ollama on `http://localhost:11434`
- Optional: Docker Desktop

### Run Full Stack (Recommended)

```bash
dotnet restore JD.Writer.sln
dotnet build JD.Writer.sln
dotnet run --project JD.Writer.AppHost/JD.Writer.AppHost.csproj
```

Then open the Aspire dashboard link shown in the terminal.

### Run Client-Only (No API)

PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "ClientOnly"
$env:AiClient__Mode = "local"
dotnet run --project JD.Writer.Web/JD.Writer.Web.csproj
```

Bash:

```bash
ASPNETCORE_ENVIRONMENT=ClientOnly AiClient__Mode=local dotnet run --project JD.Writer.Web/JD.Writer.Web.csproj
```

### Run With Docker

```bash
# client/server stack
docker compose --profile client-server up --build

# standalone client-only web
docker compose --profile client-only up --build web-standalone
```

### Run Tests

```bash
dotnet test JD.Writer.sln -c Release
```

## AI Configuration

Default values live in `JD.Writer.ApiService/appsettings.json`.

- `AI:Provider`: `auto`, `ollama`, `openai`, `fallback`
- `AI:Ollama:Endpoint`: Ollama endpoint URL
- `AI:Ollama:Model`: model name (default `llama3.2:latest`)
- `AI:OpenAI:ApiKey`: optional OpenAI key

## Documentation

Primary docs are in [`docs/`](docs):

- [Docs Home](docs/index.md)
- [Getting Started Guide](docs/GETTING_STARTED.md)
- [User Guide](docs/USER_GUIDE.md)
- [API Guide](docs/API_DOCS.md)
- [API Reference](docs/api/index.md)
- [Product Roadmap](docs/PLAN.md)
- [Delivery Backlog](docs/TODO.md)
- [Delivery Model](docs/SWARM.md)
- [Acceptance Criteria](docs/ACCEPTANCE_CRITERIA.md)
- [Spec Coverage](docs/SPEC_COVERAGE.md)
- [End-to-End Testing Guide](docs/E2E.md)
- [Accessibility Audit](docs/ACCESSIBILITY_AUDIT.md)
- [Release Models Guide](docs/RELEASE_MODELS.md)
- [Repository Publishing Guide](docs/GIT_READY_CHECKLIST.md)
- [Studio Lite (GitHub Pages)](docs/studio/index.html)

## GitHub Pages

DocFX publishes docs and Studio Lite.

- Expected docs URL: `https://jerrettdavis.github.io/JD.Writer/`
- Expected Studio Lite URL: `https://jerrettdavis.github.io/JD.Writer/studio/`

## Publishing This Repo To GitHub

If this local folder is brand new, follow the [Repository Publishing Guide](docs/GIT_READY_CHECKLIST.md).

Minimal flow:

```powershell
git add .
git commit -m "chore: initial JD.Writer baseline"
git remote add origin https://github.com/<you>/JD.Writer.git
git push -u origin main
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

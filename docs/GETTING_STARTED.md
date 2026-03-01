# Getting Started Guide

This guide gets JD.Writer running in minutes.

## Prerequisites

- .NET SDK `10.0.103` (or compatible roll-forward from `global.json`)
- Optional: Ollama running at `http://localhost:11434`
- Optional: Docker Desktop

## Choose a Runtime Mode

JD.Writer supports three primary modes:

1. `client-server` (recommended): full UI + API + AI routing
2. `client-only`: local heuristics only, no API dependency
3. `github-pages`: static Studio Lite browser app

## Run Full Stack (Recommended)

```bash
dotnet restore JD.Writer.sln
dotnet build JD.Writer.sln -c Release
dotnet run --project JD.Writer.AppHost/JD.Writer.AppHost.csproj
```

Open the Aspire dashboard URL printed in the terminal.

## Run Client-Only

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

## Run with Docker

```bash
# full client-server stack
docker compose --profile client-server up --build

# standalone client-only web app
docker compose --profile client-only up --build web-standalone
```

## Validate Your Setup

```bash
dotnet test JD.Writer.sln -c Release
```

For documentation validation:

```bash
docfx docs/docfx.json
```

## Next Steps

- Learn core workflows in the [User Guide](USER_GUIDE.md)
- Configure providers in the [API Guide](API_DOCS.md)
- Choose deployment shape in [Release Models](RELEASE_MODELS.md)

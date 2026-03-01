# Release Models

JD.Writer is intentionally shipped in multiple shapes so teams can choose the right tradeoff for development, demos, and production.

## Which Model Should You Use?

- Use `client-server` when you want full AI routing and API-backed behavior.
- Use `client-only` when you want offline-friendly local editing with no backend dependency.
- Use `docker` when you want reproducible environment packaging.
- Use `github-pages` when you want a zero-backend browser demo.

## Models at a Glance

| Model | Components | Best For |
|---|---|---|
| `client-server` | `JD.Writer.Web` + `JD.Writer.ApiService` (+ optional `JD.Writer.AppHost`) | Daily local development and full behavior validation |
| `client-only` | `JD.Writer.Web` only (`AiClient:Mode=local`) | Standalone local drafting with no API |
| `docker` | Multi-target images + compose profiles | Reproducible deployment and containerized testing |
| `github-pages` | Static `docs/studio` app + DocFX | Shareable in-browser demo with no server |

## Runtime Configuration

`JD.Writer.Web` supports:

- `AiClient:Mode`
  - `auto`: try API first, then local fallback
  - `remote`: prefer API, fallback locally on failures
  - `local`: never call API; local heuristics only
- `ApiServiceBaseUrl`: explicit URL for web-to-API calls

Examples:

```bash
# full client/server mode
export AiClient__Mode=remote
export ApiServiceBaseUrl=http://localhost:5081

# standalone client-only mode
export ASPNETCORE_ENVIRONMENT=ClientOnly
export AiClient__Mode=local
```

## Local Packaging Scripts

PowerShell:

```powershell
./scripts/release/publish-release-models.ps1
```

Bash:

```bash
./scripts/release/publish-release-models.sh
```

Output is created under `artifacts/release/`:

- `client-server/web`
- `client-server/api`
- `client-server/apphost`
- `client-only/web`
- `docker/{Dockerfile,compose.yaml,.dockerignore}`

## Docker Usage

Client/server profile:

```bash
docker compose --profile client-server up --build
```

- Web: `http://localhost:18080`
- API: `http://localhost:18081`

Client-only profile:

```bash
docker compose --profile client-only up --build web-standalone
```

- Standalone Web: `http://localhost:18082`

If Ollama runs on host, default API endpoint is `http://host.docker.internal:11434`.
Override with `AI_OLLAMA_ENDPOINT` when needed.

## GitHub Release Outputs

`.github/workflows/release.yml` publishes:

- client/server tar archives
- client-only standalone tar archive
- Docker bundle tar archive
- GHCR images:
  - `ghcr.io/<owner>/<repo>/jd-writer-api:<version>`
  - `ghcr.io/<owner>/<repo>/jd-writer-web:<version>`
  - `ghcr.io/<owner>/<repo>/jd-writer-web-standalone:<version>`

`.github/workflows/docker.yml` validates Docker targets on pull requests.

## GitHub Pages Studio Lite

DocFX publishes Studio Lite from `docs/studio/index.html`.

- Repo path: `docs/studio/`
- Published path: `https://jerrettdavis.github.io/JD.Writer/studio/`
- Storage: browser `localStorage` (`jdwriter.pages.studio.v1`)
- AI behavior: deterministic local heuristics only

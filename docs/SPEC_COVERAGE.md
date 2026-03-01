# Spec Coverage Matrix

This matrix maps each acceptance criterion to deterministic validation evidence.

| Acceptance ID | Requirement Summary | Coverage |
|---|---|---|
| AC-01 | Studio loads and supports note editing | `Command palette and slash suggestions are accessible`; `Local-first persistence survives reload` |
| AC-02 | Preview renders active markdown | `Local-first persistence survives reload` |
| AC-03 | Local state survives reload | `Local-first persistence survives reload` |
| AC-04 | Command palette keyboard open/close | `Command palette and slash suggestions are accessible` |
| AC-05 | Slash suggestions appear on `/...` | `Command palette and slash suggestions are accessible` |
| AC-06 | Slash commands execute and insert output | `Plugin slash template inserts structured markdown`; `AI API continue and slash endpoints return content` |
| AC-07 | Toolbar AI continue appends content | `AI continue appends content in the editor` |
| AC-08 | Core side panels stream insights | `Assist stream endpoint returns NDJSON chunks` |
| AC-09 | Plugin panels stream insights | `Plugin panels are loaded from manifest`; `Plugin prompt stream endpoint returns NDJSON chunks` |
| AC-10 | `/ai/continue` returns text | `AI API continue and slash endpoints return content` |
| AC-11 | `/ai/slash` returns text | `AI API continue and slash endpoints return content` |
| AC-12 | `/ai/assist/stream` returns NDJSON chunks | `Assist stream endpoint returns NDJSON chunks`; `Plugin prompt stream endpoint returns NDJSON chunks` |
| AC-13 | Provider summary exposes preference/config | `API provider summary reports ollama preference` |
| AC-14 | Ollama supported as provider | `API provider summary reports ollama preference`; `AI API continue and slash endpoints return content` |
| AC-15 | Voice capture toggle via Ctrl+M and toolbar | `Voice capture shortcut toggles and inserts transcript`; `Voice capture toolbar toggle works` |
| AC-16 | Live interim voice transcript + AI cleanup | `Voice interim transcript appears at cursor before final cleanup`; `Voice cleanup attempt is recorded in layer history` |
| AC-17 | JSON edit layers persist diff/tone metadata | `Edit layers are persisted with diff and tone metrics` |
| AC-18 | History QC panel surfaces checkpoints | `Edit layers are persisted with diff and tone metrics` |
| AC-24 | Voice session audit logs are persisted and reviewable | `Voice recordings are reviewable in persisted audit logs`; `Voice interim transcript appears at cursor before final cleanup` |
| AC-19 | System theme-aware styling | `System dark theme variables are applied` |
| AC-20 | Client/server and client-only runtime modes | `AI continue appends content in the editor`; `Client-only mode continues draft without API service` |
| AC-21 | Docker assets support both runtime profiles | `.github/workflows/docker.yml` Docker target matrix |
| AC-22 | Release includes all distribution models | `.github/workflows/release.yml`; `scripts/release/publish-release-models.ps1`; `scripts/release/publish-release-models.sh` |
| AC-23 | GitHub Pages hosts Studio Lite | `docs/studio/index.html`; `.github/workflows/docs.yml` output validation for `docs/_site/studio/index.html` |

## Notes

- AI output assertions validate for non-empty deterministic behavior, not specific model wording.
- UI/API scenarios run against local processes started by test hooks.
- Release/deployment criteria are validated in workflows and packaging scripts.

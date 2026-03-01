# JD.Writer TODO

This is the working backlog. Checked items are done in the current baseline.

## Core Experience

- [x] Replace template shell with studio layout (rail/editor/preview/tool windows)
- [x] Persist notes in `localStorage` with restore on load
- [x] Add local autocomplete from in-document corpus
- [x] Add markdown preview rendering
- [x] Add note export (`.md`)

## AI and Assist

- [x] Add `/ai/continue` API endpoint
- [x] Add `/ai/assist/stream` NDJSON streaming endpoint
- [x] Add Semantic Kernel integration path
- [x] Add fallback behavior when AI provider is unavailable
- [x] Add Ollama provider preference routing

## Platform and Delivery

- [x] Add command palette (`Ctrl+K`) and slash command flows
- [x] Add plugin manifest + dynamic tool-window loading
- [x] Add release models for `client-server` and `client-only`
- [x] Add Docker release assets and validation workflow
- [x] Add scripted publish flow for release artifacts
- [x] Add GitHub Pages Studio Lite published with DocFX

## Next High-Value Work

- [ ] Add local markdown file import/export batch workflow
- [ ] Add optional server sync API and conflict strategy
- [ ] Add voice capture/transcription provider abstraction
- [ ] Add component tests for note lifecycle and autocomplete
- [ ] Add prompt safety/output filtering policy
- [ ] Add performance budgets and telemetry dashboard

# Delivery Backlog

This backlog tracks completed baseline scope and near-term planned work.

## Completed in Current Baseline

## Core Experience

- [x] Studio layout (note rail, editor, preview, and tool windows)
- [x] Local-first note persistence and restore on reload
- [x] Local autocomplete from in-document corpus
- [x] Markdown preview rendering
- [x] Markdown export (`.md`)

## AI and Assistance

- [x] `/ai/continue` endpoint
- [x] `/ai/assist/stream` NDJSON streaming endpoint
- [x] Semantic Kernel integration path
- [x] Provider fallback behavior when AI is unavailable
- [x] Ollama-first provider routing support

## Platform and Delivery

- [x] Command palette (`Ctrl+K`) and slash command workflow
- [x] Plugin manifest and dynamic side-panel loading
- [x] Runtime release models (`client-server` and `client-only`)
- [x] Docker release assets and Docker workflow validation
- [x] Release packaging scripts
- [x] GitHub Pages Studio Lite publishing with DocFX

## Planned Next Work

- [ ] Local markdown import/export batch workflow
- [ ] Optional server sync API and conflict handling strategy
- [ ] Voice transcription provider abstraction layer
- [ ] Component tests for note lifecycle and autocomplete
- [ ] Prompt safety and output filtering policy
- [ ] Performance budgets and dashboard telemetry

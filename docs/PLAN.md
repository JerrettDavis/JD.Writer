# JD.Writer Plan

## Product Goal

Build a markdown workspace that is fast enough for capture and structured enough for serious iteration.

The core promise is:

- local-first by default
- server-capable when needed
- graceful fallback when AI or network is unavailable

## v1 Architecture

### `JD.Writer.Web`

- Primary workspace: note rail, editor, preview, side insights
- Browser persistence (`localStorage`) for offline-safe drafts
- Keyboard-first interactions (palette, slash commands, continue, voice toggle)

### `JD.Writer.ApiService`

- `/ai/continue`: append draft continuation
- `/ai/assist/stream`: stream hints/help/brainstorm lines
- `/ai/slash`: run slash transforms
- Provider strategy: Ollama and Semantic Kernel/OpenAI with deterministic fallback

### `JD.Writer.AppHost`

- Runs web + API together in local full-stack mode
- Adds Aspire orchestration, discovery, and health checks

## Milestones

1. Foundation
- Studio UX and local persistence
- Markdown preview
- AI continue and streamed sidebars

2. Extensibility
- Plugin contracts for transforms and panel tools
- Prompt profiles and pluggable commands

3. Reliability and sync
- Optional server sync and conflict handling
- Better operational telemetry and resilience budgets

4. Voice and multimodal
- Browser-first dictation
- Optional transcription providers
- Better transcript-to-markdown workflows

## Current Non-Goals

- Multi-user real-time collaboration
- Enterprise permission model
- Large-scale vector search/indexing

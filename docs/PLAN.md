# Product Roadmap

This roadmap explains where JD.Writer is headed and how current architecture supports that path.

## Product Direction

JD.Writer is designed for high-speed markdown capture with structured AI-assisted iteration.

Core principles:

- local-first by default
- server-available when AI services are needed
- deterministic fallback when providers are unavailable

## Current Architecture (v1)

## `JD.Writer.Web`

- Primary authoring experience: note rail, editor, preview, and insight panels
- Browser persistence for offline-safe drafts
- Keyboard-first controls including command palette, slash commands, AI continue, and voice toggle

## `JD.Writer.ApiService`

- `/ai/continue` for continuation generation
- `/ai/slash` for command-based transforms
- `/ai/assist/stream` for NDJSON insight streaming
- Provider routing across Ollama/Semantic Kernel/OpenAI with graceful fallback

## `JD.Writer.AppHost`

- Local full-stack orchestration for web + API
- Aspire-based discovery and health wiring

## Milestone Themes

1. Foundation (complete in v1 baseline)
- Local-first studio
- Markdown preview
- AI continue and streaming sidebars

2. Extensibility
- Plugin contracts for commands and panel tools
- Expandable prompt and command profiles

3. Reliability and Sync
- Optional server sync and conflict strategy
- Operational telemetry and resilience targets

4. Voice and Multimodal
- Browser-first dictation pipeline
- Optional transcription providers
- Better transcript cleanup and formatting workflows

## Explicit Non-Goals (v1)

- Multi-user real-time collaboration
- Enterprise IAM/permissions model
- Large-scale vector index/search infrastructure

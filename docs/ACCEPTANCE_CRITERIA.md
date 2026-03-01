# Acceptance Criteria (v1)

Scope date: February 27, 2026

This document defines the functional quality bar for JD.Writer v1.

## Core Studio

- AC-01: Users can open the studio, create/select notes, and edit markdown in a primary editor.
- AC-02: Markdown preview renders the active note content.
- AC-03: Local-first state persists note title/content across browser reload.

## Authoring Ergonomics

- AC-04: Command palette opens with keyboard (`Ctrl+K`) and can be dismissed.
- AC-05: Slash command suggestions appear while typing `/...` in the editor.
- AC-06: Built-in and plugin slash commands execute and insert output into the draft.

## AI-Assisted Workflows

- AC-07: Toolbar AI continue appends generated content to the active draft.
- AC-08: Side panels (Hints/Help/Brainstorm) stream insight items.
- AC-09: Plugin-defined side panels load from plugin manifest and stream insight items.
- AC-15: Voice capture can be toggled from the editor via keyboard (`Ctrl+M`) and toolbar.
- AC-16: Interim voice transcript streams at the current cursor position with near-immediate feedback, and finalized transcript gets a best-effort cleanup pass.

## API and Provider Behavior

- AC-10: `/ai/continue` returns continuation text.
- AC-11: `/ai/slash` returns transformed output.
- AC-12: `/ai/assist/stream` returns NDJSON stream chunks.
- AC-13: API provider summary exposes configured provider preference and Ollama readiness.
- AC-14: Ollama is supported as a first-class provider option.

## Document Layering and QC

- AC-17: Note edits are persisted as JSON layers with operation/source metadata, diff metrics, and tone metrics.
- AC-18: History QC panel shows recent layer checkpoints and tone drift indicators for the active note.
- AC-24: Voice capture sessions persist transcript/audit events (interim, finalized, inserted, cleanup outcomes) and are reviewable in a dedicated Voice Review panel.

## Theme and Readability

- AC-19: Studio honors system theme preference and applies dark/light tokens via media query support.

## Release and Deployment

- AC-20: Web runtime supports `client-server` (API-backed) and `client-only` (local heuristic) modes.
- AC-21: Docker assets support `client-server` and `client-only` compose profiles.
- AC-22: Release pipeline publishes packaged artifacts for client/server, client-only, and Docker bundle distributions.
- AC-23: GitHub Pages publishes a working browser-only studio app at `/studio/` alongside DocFX docs.

## Out of Scope (v1)

- Server sync/conflict resolution
- Multi-user collaboration

# Delivery Model

This document describes a scalable engineering delivery model for JD.Writer.

## Objective

Ship quickly without degrading long-term maintainability, especially around plugins, AI providers, and local-first data behavior.

## Workstreams

1. Editor and UX
- Owns note rail, editor interactions, preview rendering, and keyboard ergonomics
- Owns command palette, slash UX, and visual consistency

2. AI Runtime
- Owns prompt composition, stream behavior, provider routing, and fallback quality
- Owns Ollama/OpenAI/Semantic Kernel integration reliability

3. Data and Local-First Layer
- Owns note storage format, edit layers, and import/export paths
- Owns future sync and conflict strategy

4. Voice and Capture
- Owns dictation/transcript pipeline and cursor-position insertion behavior
- Owns transcription provider abstraction roadmap

5. Platform and Quality
- Owns CI/CD, release workflows, accessibility standards, and performance guardrails
- Owns publish hardening for client/server, client-only, Docker, and Pages

## Team Coordination Rhythm

1. Daily integration sync for contract-level changes
2. Shared schemas for request/response and NDJSON stream chunk formats
3. Weekly architecture review focused on migration boundaries and plugin contracts

## Definition of Done

- Feature is shippable within its intended runtime mode
- Deterministic validation exists (automated tests or explicit verification step)
- User-facing docs in `docs/` are updated

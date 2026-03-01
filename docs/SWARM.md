# JD.Writer Swarm Plan

This file describes how work can be split in parallel without stepping on each other.

## Objective

Ship quickly while keeping architecture clean enough for plugins, sync, and voice features.

## Lanes

1. Editor and UX
- Owns note rail, editor behavior, preview, and keyboard ergonomics
- Owns palette/slash UX and visual consistency

2. AI Runtime
- Owns prompt templates, streaming behavior, provider routing, and fallback quality
- Owns Ollama/OpenAI/Semantic Kernel integration reliability

3. Data and Local-First Layer
- Owns storage format, revision history, and import/export paths
- Owns future sync/conflict strategy

4. Voice and Capture
- Owns dictation pipeline, transcript transforms, and insertion behaviors
- Owns future provider abstraction for transcription

5. Platform and Quality
- Owns CI/CD, release workflows, accessibility, and performance guardrails
- Owns release and publish hardening

## Coordination Rhythm

1. Daily short integration sync for contract changes
2. Shared schema/contracts for request/response and stream chunk formats
3. Weekly architecture review focused on migration boundaries and plugin contracts

## Definition of Done

- Feature is shippable behind its intended mode/flag
- Validation exists (test evidence or deterministic check)
- Docs are updated in `docs/`

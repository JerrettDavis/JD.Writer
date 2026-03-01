# Changelog

All notable changes to JD.Writer are tracked here.

This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Local-first markdown studio with note rail, editor, and live preview
- Command palette, slash commands, plugin manifest support, and AI-assisted transforms
- Voice capture (`Ctrl+M`) with transcript insertion and cleanup workflow
- JSON edit layers with diff and tone metrics for history/QC analysis
- Theme-aware UI with preview-only render theme switching and persistence
- Reqnroll + Playwright end-to-end suite mapped to acceptance criteria
- CI/CD workflows for build, docs, integration, quality, and release
- Nerdbank.GitVersioning baseline (`version.json`) and hardened build settings
- DocFX docs site plus GitHub Pages Studio Lite client-only app

### Changed

- Runtime now cleanly supports `client-server`, `client-only`, Docker, and GitHub Pages distribution paths
- AppHost health-check wiring corrected to avoid local dashboard endpoint discovery false negatives

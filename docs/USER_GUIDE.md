# User Guide

This guide covers day-to-day authoring workflows in JD.Writer.

## Workspace Overview

The studio is organized into four areas:

- Note rail: create, select, and organize notes
- Editor: primary markdown authoring surface
- Preview: rendered markdown view
- Insight panels: hints/help/brainstorm streams

## Core Authoring Flow

1. Create a note from the rail.
2. Draft in markdown in the editor.
3. Use preview to check formatting and structure.
4. Iterate using AI continue, slash commands, and panel hints.

## Keyboard Shortcuts

- `Ctrl+K`: open command palette
- `Ctrl+M`: toggle voice capture and transcript insertion
- `/` in editor: open slash command suggestions

## AI Workflows

- `AI Continue`: appends contextual continuation for the active draft
- Slash commands: transform or scaffold selected content
- Insight panels: stream hints/help/brainstorm suggestions while you write

If no API is available, the client falls back to deterministic local behavior.

## Voice Workflow

1. Place the cursor where text should be inserted.
2. Press `Ctrl+M` (or use toolbar voice control) to start capture.
3. Speak and watch interim text stream directly at the cursor position.
4. Finalized transcript is committed and JD.Writer runs an asynchronous cleanup pass.
5. Voice transcript and cleanup operations are recorded in history layers and the `Voice Review` panel.

## Themes and Readability

- Studio UI follows system light/dark preference.
- Markdown render themes can be changed independently from site theme.
- Theme selections persist in browser storage.

## History, Layering, and QC

Each edit is stored as a JSON layer with:

- operation metadata (source/action)
- diff metrics
- tone metrics

This enables historical review, consistency checks, and tone drift monitoring over time.

## Plugins

Plugins are discovered from `JD.Writer.Web/wwwroot/plugins/plugins.json`.

Plugin capabilities can include:

- slash commands
- additional insight panel feeds
- draft transforms

## Export

- Export active note as markdown (`.md`) from the studio controls.

# API Guide

This guide documents JD.Writer API behavior for local development and integrations.

Base URL (default local): `http://localhost:5081`

## Endpoints

## `POST /ai/continue`

Generates continuation text for a draft.

Request body:

```json
{
  "text": "# Draft title\nExisting content...",
  "cursorPosition": 42
}
```

Response body:

```json
{
  "continuation": "\n\n## Next Steps\n..."
}
```

## `POST /ai/slash`

Executes a slash command transform.

Request body:

```json
{
  "command": "summarize",
  "text": "Long markdown content..."
}
```

Response body:

```json
{
  "output": "Short summary..."
}
```

## `POST /ai/assist/stream`

Streams NDJSON insight chunks for side panels.

Request body:

```json
{
  "text": "Current note text...",
  "panel": "hints"
}
```

Response content type:

`application/x-ndjson`

Example chunks:

```json
{"item":"Consider adding an explicit goal statement."}
{"item":"Split this section into short bullets for readability."}
```

## `GET /ai/provider-summary`

Returns active provider preference and readiness diagnostics (including Ollama readiness).

## Provider Configuration

Primary configuration file: `JD.Writer.ApiService/appsettings.json`

Key settings:

- `AI:Provider`: `auto`, `ollama`, `openai`, `fallback`
- `AI:Ollama:Endpoint`: Ollama URL
- `AI:Ollama:Model`: model name
- `AI:OpenAI:ApiKey`: OpenAI API key (optional)

## Curl Quick Checks

```bash
curl -sS http://localhost:5081/ai/provider-summary
```

```bash
curl -sS -X POST http://localhost:5081/ai/continue \
  -H "Content-Type: application/json" \
  -d "{\"text\":\"Draft intro\",\"cursorPosition\":11}"
```

```bash
curl -N -X POST http://localhost:5081/ai/assist/stream \
  -H "Content-Type: application/json" \
  -d "{\"text\":\"Draft intro\",\"panel\":\"hints\"}"
```

## Reference API Docs

For generated code-level API documentation, see [API Reference](api/index.md).

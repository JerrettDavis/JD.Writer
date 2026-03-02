# API Guide

This guide documents JD.Writer API behavior for local development and integrations.

Base URL (default local): `http://localhost:19081` (E2E/test host) or `http://localhost:5319` (direct `dotnet run`)

## Endpoints

## `POST /ai/continue`

Generates continuation text for a draft.

Request body:

```json
{
  "draft": "# Draft title\nExisting content..."
}
```

Response body:

```json
{
  "continuation": "\n\n## Next Steps\n...",
  "source": "ollama"
}
```

## `POST /ai/slash`

Executes a slash command transform.

Request body:

```json
{
  "command": "summarize",
  "draft": "Long markdown content...",
  "prompt": "Optional custom prompt override"
}
```

Response body:

```json
{
  "output": "Short summary...",
  "source": "native-llama-cpu"
}
```

## `POST /ai/assist/stream`

Streams NDJSON insight chunks for side panels.

Request body:

```json
{
  "mode": "hints",
  "draft": "Current note text...",
  "prompt": "Optional panel prompt override"
}
```

Response content type:

`application/x-ndjson`

Example chunks:

```json
{"text":"Consider adding an explicit goal statement.","source":"ollama"}
{"text":"Split this section into short bullets for readability.","source":"native-llama-gpu"}
```

## `GET /ai/provider-summary`

Returns active provider preference and readiness diagnostics:
- openai configured status
- ollama model/endpoint readiness inputs
- native llama chain configuration
- hardware scan summary (GPU names + preferred acceleration)
- active runtime provider order

## Provider Configuration

Primary configuration file: `JD.Writer.ApiService/appsettings.json`

Key settings:

- `AI:Provider`: `auto`, `ollama`, `native`, `local`, `openai`, `fallback`
- `AI:Ollama:Endpoint`: Ollama URL
- `AI:Ollama:Model`: model name
- `AI:OpenAI:ApiKey`: OpenAI API key (optional)
- `AI:NativeLlama:Enabled`: enable native llama chain (default `true`)
- `AI:NativeLlama:CliPath`: full path to `llama-cli`/`main` executable
- `AI:NativeLlama:ModelDirectory`: optional directory scanned for `.gguf` models
- `AI:NativeLlama:GpuModelPath`: GGUF model path for GPU-capable runs
- `AI:NativeLlama:CpuModelPath`: GGUF model path for CPU fallback runs
- `AI:NativeLlama:ContextSize`, `MaxTokens`, `Threads`, `GpuLayers`, `Temperature`, `TimeoutSeconds`

Auto-discovery behavior:
- If `CliPath` is empty, runtime probes `PATH` for `llama-cli`, `main`, or `llamafile`.
- If `GpuModelPath`/`CpuModelPath` are empty, runtime probes `ModelDirectory` then common local roots for `.gguf`.

## Curl Quick Checks

```bash
curl -sS http://localhost:5319/ai/provider-summary
```

```bash
curl -sS -X POST http://localhost:5319/ai/continue \
  -H "Content-Type: application/json" \
  -d "{\"draft\":\"Draft intro\"}"
```

```bash
curl -N -X POST http://localhost:5319/ai/assist/stream \
  -H "Content-Type: application/json" \
  -d "{\"mode\":\"hints\",\"draft\":\"Draft intro\"}"
```

## Reference API Docs

For generated code-level API documentation, see [API Reference](api/index.md).

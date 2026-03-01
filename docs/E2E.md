# End-to-End Testing Guide

Project: `JD.Writer.E2E`

## Purpose

The E2E suite validates user-critical workflows across UI and API boundaries.

## Coverage Focus

- Studio load, note editing, and markdown preview workflows
- Command palette and slash command keyboard interactions
- Voice capture toggling, live interim transcript flow, cleanup behavior, and persisted Voice Review audit logs
- AI endpoint behavior including NDJSON streaming
- Client-only continuation behavior without API dependency

## Test Runtime Topology

- API host: `http://127.0.0.1:19081`
- Web host (full stack): `http://127.0.0.1:19080`
- Web host (client-only mode): `http://127.0.0.1:19082`

Full-stack web test host routes to API through `ApiServiceBaseUrl`.

## Browser Provisioning

Playwright Chromium is installed automatically by test hooks.

Set `JDWRITER_SKIP_PLAYWRIGHT_INSTALL=1` to skip install when browser binaries are already present.

## Run the Suite

```powershell
dotnet test C:/git/JD.Writer/JD.Writer.E2E/JD.Writer.E2E.csproj -c Release
```

## CI Expectations

E2E scenarios are part of pull request validation and should stay green for merge readiness.

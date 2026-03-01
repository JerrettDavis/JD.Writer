# E2E Notes

Project: `JD.Writer.E2E`

## What These Tests Protect

- Core workspace loads and remains editable
- Command palette and slash interactions work from keyboard
- Voice capture interactions are deterministic in test mode
- API routes produce valid output and stream payloads
- Client-only mode still provides working AI continuation behavior

## Runtime Topology Used By Tests

- API host: `http://127.0.0.1:19081`
- Web host (full stack): `http://127.0.0.1:19080`
- Web host (client-only scenario): `http://127.0.0.1:19082`

The web host points to the API via `ApiServiceBaseUrl` for deterministic local routing in full-stack tests.

## Browser Install Behavior

By default, test hooks install Chromium via Playwright.

To skip browser install when already present:

- set `JDWRITER_SKIP_PLAYWRIGHT_INSTALL=1`

## Run

```powershell
dotnet test C:/git/JD.Writer/JD.Writer.E2E/JD.Writer.E2E.csproj -c Release
```

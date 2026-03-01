# Contributing

Thanks for helping build JD.Writer.

This project moves quickly, so the goal of this guide is simple: make changes easy to review, safe to merge, and easy to ship.

## Local Setup

```bash
dotnet restore JD.Writer.sln
dotnet build JD.Writer.sln
dotnet test JD.Writer.sln -c Release
```

Optional checks:

```bash
docfx docs/docfx.json
docker compose config --quiet
```

## Branching

- Branch from `main`
- Keep one concern per branch
- Use readable branch names such as `feature/client-only-sync` or `fix/voice-cleanup-range`

## Coding Expectations

- Follow `.editorconfig`
- Keep docs aligned with behavior changes
- Update acceptance/spec docs for user-facing changes
- Add or update E2E coverage for changed user journeys

## PR Checklist

Before opening a pull request:

1. Build succeeds locally.
2. Tests pass locally.
3. User-visible changes are documented.
4. `CHANGELOG.md` has an entry for user-visible behavior.
5. PR description includes what changed, why, and how it was validated.

## Commit Messages

Conventional commit style is preferred:

- `feat: ...`
- `fix: ...`
- `docs: ...`
- `chore: ...`
- `test: ...`

## Security

If your change affects authentication, data handling, or AI/provider boundaries, include a short risk note in your PR.

For vulnerability reporting, see [SECURITY.md](SECURITY.md).

# Repository Publishing Guide

Use this guide to prepare JD.Writer for clean GitHub publication and handoff.

## 1. Validate Locally

```powershell
dotnet restore JD.Writer.sln
dotnet build JD.Writer.sln -c Release
dotnet test JD.Writer.sln -c Release
docfx docs/docfx.json
docker compose config --quiet
```

## 2. Confirm Repository Hygiene

- `.gitignore` covers generated outputs (`bin/`, `obj/`, `artifacts/`, `docs/_site/`, test results)
- No secrets are present in tracked files
- Documentation reflects current behavior and release paths

## 3. Stage and Review

```powershell
git status
git add .
git status
```

Review the staged diff before committing.

## 4. Commit Baseline

```powershell
git commit -m "chore: initialize JD.Writer"
```

## 5. Connect Remote and Push

```powershell
git remote add origin https://github.com/<owner>/JD.Writer.git
git branch -M main
git push -u origin main
```

## 6. Confirm GitHub Repository Settings

- GitHub Actions enabled
- GitHub Pages configured to deploy from Actions
- Required package/release permissions and secrets configured

## 7. Verify Hosted Outputs

After docs workflow completion:

- Docs: `https://<owner>.github.io/JD.Writer/`
- Studio Lite: `https://<owner>.github.io/JD.Writer/studio/`

## 8. Create First Release (Optional)

```powershell
git tag v0.1.0
git push origin v0.1.0
```

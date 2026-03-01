# Git Ready Checklist

Use this checklist when you want to publish or hand off this repository cleanly.

## 1. Validate Locally

```powershell
dotnet restore JD.Writer.sln
dotnet build JD.Writer.sln -c Release
dotnet test JD.Writer.sln -c Release
docfx docs/docfx.json
docker compose config --quiet
```

## 2. Confirm Repo Hygiene

- Check `.gitignore` includes generated artifacts (`bin/`, `obj/`, `artifacts/`, `docs/_site/`, test results)
- Confirm no secrets in tracked files
- Confirm docs reflect current behavior and release models

## 3. Stage and Review

```powershell
git status
git add .
git status
```

Review staged diff before commit.

## 4. First Commit (if this repo is new)

```powershell
git commit -m "chore: initialize JD.Writer"
```

## 5. Connect Remote and Push

```powershell
git remote add origin https://github.com/<owner>/JD.Writer.git
git branch -M main
git push -u origin main
```

## 6. Confirm GitHub Settings

- Enable GitHub Actions
- Enable GitHub Pages (source: Actions)
- Confirm required secrets (if any) for release publishing

## 7. Verify Hosted Outputs

After the docs workflow completes:

- Docs: `https://<owner>.github.io/JD.Writer/`
- Studio Lite: `https://<owner>.github.io/JD.Writer/studio/`

## Optional First Release

Create a version tag to trigger release packaging:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

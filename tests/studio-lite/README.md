# Studio Lite Playwright Suite

Playwright coverage for the public Studio Lite demo:

- Target URL: `https://jerrettdavis.github.io/JD.Writer/studio/index.html`
- Focus: demo robustness (layout, editing, commands, themes, export)

## Run Locally

```bash
cd tests/studio-lite
npm install
npx playwright install
npm test
```

## Reports

- HTML report: `tests/studio-lite/playwright-report/index.html`
- JUnit XML: `tests/studio-lite/artifacts/playwright-junit.xml`

Open the HTML report:

```bash
npm run report
```

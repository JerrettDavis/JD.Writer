const { test, expect } = require("@playwright/test");

async function resetStudio(page) {
  await page.goto("studio/index.html", { waitUntil: "domcontentloaded" });
  await page.evaluate(() => {
    localStorage.removeItem("jdwriter.pages.studio.v2");
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: "Client-Only Markdown Studio" })).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await resetStudio(page);
});

test("renders full studio shell with core panes", async ({ page }) => {
  await expect(page.locator(".notes-pane")).toBeVisible();
  await expect(page.locator(".editor-pane")).toBeVisible();
  await expect(page.locator(".insights-pane")).toBeVisible();
  await expect(page.locator("#hints-list li")).toHaveCount(5);
  await expect(page.locator("#help-list li")).toHaveCount(5);
  await expect(page.locator("#brainstorm-list li")).toHaveCount(5);
});

test("creates note and keeps markdown preview in sync", async ({ page }) => {
  const noteItems = page.locator("#note-list .note-item");
  const before = await noteItems.count();

  await page.click("#new-note");
  await page.fill("#note-title", "Demo Plan");
  await page.fill("#note-content", "# Demo Plan\n\n- item one\n- item two");

  await expect(noteItems).toHaveCount(before + 1);
  await expect(page.locator("#preview h1")).toContainText("Demo Plan");
  await expect(page.locator("#preview li")).toHaveCount(2);
});

test("shows slash command suggestions while typing", async ({ page }) => {
  await page.fill("#note-content", "/su");
  await expect(page.locator("#slash-bar")).toBeVisible();
  await expect(page.locator("#slash-bar .slash-item").first()).toContainText("/summarize");
});

test("opens command palette and executes create note command", async ({ page }) => {
  const noteItems = page.locator("#note-list .note-item");
  const before = await noteItems.count();

  await page.keyboard.press("Control+K");
  await expect(page.locator("#palette")).toBeVisible();
  await page.fill("#palette-input", "create note");
  await page.keyboard.press("Enter");

  await expect(page.locator("#palette")).toBeHidden();
  await expect(noteItems).toHaveCount(before + 1);
});

test("switches preview render themes cleanly", async ({ page }) => {
  await page.selectOption("#preview-theme", "terminal");
  await expect(page.locator("#preview")).toHaveClass(/theme-terminal/);

  await page.selectOption("#preview-theme", "blueprint");
  await expect(page.locator("#preview")).toHaveClass(/theme-blueprint/);
});

test("ai continue appends additional content", async ({ page }) => {
  await page.fill("#note-content", "This is a deterministic demo seed.");
  const beforeLength = await page.locator("#note-content").evaluate((el) => el.value.length);

  await page.click("#continue-button");

  await expect
    .poll(async () => page.locator("#note-content").evaluate((el) => el.value.length), {
      timeout: 15_000
    })
    .toBeGreaterThan(beforeLength);
});

test("exports markdown with expected file naming", async ({ page }) => {
  await page.fill("#note-title", "Export Demo");
  await page.fill("#note-content", "# Export Demo\n\nBody text.");

  const [download] = await Promise.all([
    page.waitForEvent("download"),
    page.click("#export-button")
  ]);

  expect(download.suggestedFilename()).toMatch(/export-demo\.md$/);
});

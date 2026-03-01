using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using JD.Writer.E2E.Support;

namespace JD.Writer.E2E.Steps;

[Binding]
public sealed class StudioSteps
{
    private readonly ScenarioContext _scenarioContext;

    public StudioSteps(ScenarioContext scenarioContext)
    {
        _scenarioContext = scenarioContext;
    }

    [Given("JD.Writer is running for e2e tests")]
    public async Task GivenJdWriterIsRunningForE2eTests()
    {
        await TestHostManager.EnsureStartedAsync();
        _scenarioContext.GetState().WebBaseUrl = TestHostManager.WebBaseUrl;
    }

    [Given("JD.Writer client-only mode is running for e2e tests")]
    public async Task GivenJdWriterClientOnlyModeIsRunningForE2eTests()
    {
        await TestHostManager.EnsureStandaloneClientOnlyStartedAsync();
        _scenarioContext.GetState().WebBaseUrl = TestHostManager.StandaloneWebBaseUrl;
    }

    [When("I open the studio home page")]
    public async Task WhenIOpenTheStudioHomePage()
    {
        var state = _scenarioContext.GetState();
        var page = state.Page!;
        await page.GotoAsync(state.WebBaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".studio-header h1")).ToContainTextAsync("Markdown Studio");
        await EnsureEditorReadyAsync(page);
    }

    [When("I open the command palette with keyboard")]
    public async Task WhenIOpenTheCommandPaletteWithKeyboard()
    {
        var page = _scenarioContext.GetState().Page!;
        await EnsureEditorReadyAsync(page);
        await page.Locator("textarea.editor-input").ClickAsync();
        await page.Keyboard.PressAsync("Control+K");
    }

    [When("I enable voice test mode")]
    public async Task WhenIEnableVoiceTestMode()
    {
        var page = _scenarioContext.GetState().Page!;
        await page.EvaluateAsync("window.JDWriterStudio.setDictationTestMode(true)");
    }

    [When("I place cursor at the end of the editor")]
    public async Task WhenIPlaceCursorAtTheEndOfTheEditor()
    {
        var page = _scenarioContext.GetState().Page!;
        var editor = page.Locator("textarea.editor-input");
        await editor.ClickAsync();
        await editor.PressAsync("End");
    }

    [When("I toggle voice capture with keyboard")]
    public async Task WhenIToggleVoiceCaptureWithKeyboard()
    {
        var page = _scenarioContext.GetState().Page!;
        var editor = page.Locator("textarea.editor-input");
        await editor.ClickAsync();
        await editor.PressAsync("Control+M");
        await page.WaitForTimeoutAsync(150);
    }

    [When("I toggle voice capture from the toolbar")]
    public async Task WhenIToggleVoiceCaptureFromTheToolbar()
    {
        var page = _scenarioContext.GetState().Page!;
        await page.Locator("button.ghost-button", new PageLocatorOptions { HasTextString = "Mic (" }).First.ClickAsync();
        await page.WaitForTimeoutAsync(200);
    }

    [When(@"I inject voice transcript ""(.*)""")]
    public async Task WhenIInjectVoiceTranscript(string transcript)
    {
        var page = _scenarioContext.GetState().Page!;
        var escaped = JsonSerializer.Serialize(transcript);
        await page.EvaluateAsync($"window.JDWriterStudio.emitTestTranscript({escaped})");
        await page.WaitForTimeoutAsync(300);
    }

    [When(@"I inject interim voice transcript ""(.*)""")]
    public async Task WhenIInjectInterimVoiceTranscript(string transcript)
    {
        var page = _scenarioContext.GetState().Page!;
        var escaped = JsonSerializer.Serialize(transcript);
        await page.EvaluateAsync($"window.JDWriterStudio.emitTestInterimTranscript({escaped})");
        await page.WaitForTimeoutAsync(80);
    }

    [When(@"I finalize voice transcript ""(.*)""")]
    public async Task WhenIFinalizeVoiceTranscript(string transcript)
    {
        var page = _scenarioContext.GetState().Page!;
        var escaped = JsonSerializer.Serialize(transcript);
        await page.EvaluateAsync($"window.JDWriterStudio.emitTestFinalTranscript({escaped})");
        await page.WaitForTimeoutAsync(250);
    }

    [Then("I should see the command palette")]
    public async Task ThenIShouldSeeTheCommandPalette()
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator(".command-palette")).ToBeVisibleAsync();
    }

    [Then(@"voice status should contain ""(.*)""")]
    public async Task ThenVoiceStatusShouldContain(string value)
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator(".header-status")).ToContainTextAsync(value);
    }

    [Then(@"the voice toolbar button should contain ""(.*)""")]
    public async Task ThenTheVoiceToolbarButtonShouldContain(string value)
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator("button.ghost-button", new PageLocatorOptions { HasTextString = "Mic (" }).First).ToContainTextAsync(value);
    }

    [When("I close the command palette")]
    public async Task WhenICloseTheCommandPalette()
    {
        var page = _scenarioContext.GetState().Page!;
        var palette = page.Locator(".command-palette");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (await palette.CountAsync() == 0)
            {
                return;
            }

            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(120);
            if (await palette.CountAsync() == 0)
            {
                return;
            }

            var backdrop = page.Locator(".palette-backdrop");
            if (await backdrop.CountAsync() > 0)
            {
                await backdrop.ClickAsync(new LocatorClickOptions
                {
                    Force = true
                });
                await page.WaitForTimeoutAsync(120);
            }
        }

        await Assertions.Expect(palette).ToHaveCountAsync(0);
    }

    [When(@"I type ""(.*)"" in the editor")]
    public async Task WhenITypeInTheEditor(string text)
    {
        var page = _scenarioContext.GetState().Page!;
        var editor = page.Locator("textarea.editor-input");
        await editor.ClickAsync();
        await editor.FillAsync(Normalize(text));
        await page.WaitForTimeoutAsync(300);
    }

    [When("I inject corrupted local workspace state")]
    public async Task WhenIInjectCorruptedLocalWorkspaceState()
    {
        var page = _scenarioContext.GetState().Page!;
        var corruptedState = JsonSerializer.Serialize(new
        {
            activeNoteId = "broken-note",
            previewTheme = "studio",
            notes = new[]
            {
                new
                {
                    id = "broken-note",
                    title = (string?)null,
                    content = (string?)null,
                    updatedAt = DateTimeOffset.UtcNow,
                    layers = new object?[]
                    {
                        null
                    }
                }
            }
        });

        await page.EvaluateAsync(
            "(raw) => window.localStorage.setItem('jdwriter.state.v1', raw)",
            corruptedState);
    }

    [When("I inject oversized local workspace state")]
    public async Task WhenIInjectOversizedLocalWorkspaceState()
    {
        var page = _scenarioContext.GetState().Page!;
        var hugeContent = "# Oversized state\n\n" + new string('x', 140_000);
        var oversizedState = JsonSerializer.Serialize(new
        {
            activeNoteId = "large-note",
            previewTheme = "studio",
            notes = new[]
            {
                new
                {
                    id = "large-note",
                    title = "Large note",
                    content = hugeContent,
                    updatedAt = DateTimeOffset.UtcNow,
                    layers = new object?[]
                    {
                        new
                        {
                            layerId = "large-layer",
                            createdAt = DateTimeOffset.UtcNow,
                            updatedAt = DateTimeOffset.UtcNow,
                            operation = "manual-edit",
                            source = "local",
                            titleBefore = "Large note",
                            titleAfter = "Large note",
                            snapshot = hugeContent
                        }
                    }
                }
            }
        });

        await page.EvaluateAsync(
            "(raw) => window.localStorage.setItem('jdwriter.state.v1', raw)",
            oversizedState);
    }

    [Then("I should see slash command suggestions")]
    public async Task ThenIShouldSeeSlashCommandSuggestions()
    {
        var page = _scenarioContext.GetState().Page!;
        var popover = page.Locator(".slash-popover");
        await Assertions.Expect(popover).ToBeVisibleAsync();
        var count = await popover.Locator(".slash-item").CountAsync();
        Assert.That(count, Is.GreaterThan(0));
    }

    [When(@"I select slash command ""(.*)""")]
    public async Task WhenISelectSlashCommand(string commandName)
    {
        var page = _scenarioContext.GetState().Page!;
        var command = page.Locator(".slash-item").Filter(new LocatorFilterOptions
        {
            HasTextString = "/" + commandName
        });

        await command.First.ClickAsync();
    }

    [Then(@"the editor should contain ""(.*)""")]
    public async Task ThenTheEditorShouldContain(string value)
    {
        var page = _scenarioContext.GetState().Page!;
        await WaitForEditorToContainAsync(page, Normalize(value), TimeSpan.FromSeconds(10));
    }

    [Then(@"the studio title should contain ""(.*)""")]
    public async Task ThenTheStudioTitleShouldContain(string title)
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator(".studio-header h1")).ToContainTextAsync(title);
    }

    [When(@"I set note title to ""(.*)""")]
    public async Task WhenISetNoteTitleTo(string value)
    {
        var page = _scenarioContext.GetState().Page!;
        var title = page.Locator("input.title-input");
        await title.ClickAsync();
        await title.FillAsync(value);
    }

    [When("I reload the page")]
    public async Task WhenIReloadThePage()
    {
        var page = _scenarioContext.GetState().Page!;
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await EnsureEditorReadyAsync(page);
    }

    [When(@"I choose preview render theme ""(.*)""")]
    public async Task WhenIChoosePreviewRenderTheme(string themeId)
    {
        var page = _scenarioContext.GetState().Page!;
        await page.Locator("[data-testid='preview-render-theme']").SelectOptionAsync(themeId);
        await page.WaitForTimeoutAsync(150);
    }

    [Then(@"preview render theme should be ""(.*)""")]
    public async Task ThenPreviewRenderThemeShouldBe(string themeId)
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator("[data-testid='preview-render-theme']")).ToHaveValueAsync(themeId);
    }

    [Then(@"preview should use render class ""(.*)""")]
    public async Task ThenPreviewShouldUseRenderClass(string className)
    {
        var page = _scenarioContext.GetState().Page!;
        var classPattern = new Regex($@"(^|\s){Regex.Escape(className)}(\s|$)");
        await Assertions.Expect(page.Locator("[data-testid='preview-content']")).ToHaveClassAsync(classPattern);
    }

    [When("I emulate dark color scheme")]
    public async Task WhenIEmulateDarkColorScheme()
    {
        var page = _scenarioContext.GetState().Page!;
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await page.WaitForTimeoutAsync(100);
    }

    [Then(@"the note title input should be ""(.*)""")]
    public async Task ThenTheNoteTitleInputShouldBe(string value)
    {
        var page = _scenarioContext.GetState().Page!;
        await Assertions.Expect(page.Locator("input.title-input")).ToHaveValueAsync(value);
    }

    [When("I trigger AI continue from the toolbar")]
    public async Task WhenITriggerAiContinueFromTheToolbar()
    {
        var page = _scenarioContext.GetState().Page!;
        var editor = page.Locator("textarea.editor-input");
        var before = await editor.InputValueAsync();
        _scenarioContext.GetState().EditorLengthBeforeAction = before.Length;

        var continueButton = page.GetByRole(AriaRole.Button, new() { Name = "AI Continue" });
        await Assertions.Expect(continueButton).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 15000
        });
        await continueButton.ClickAsync();
    }

    [Then("editor content should be longer than before")]
    public async Task ThenEditorContentShouldBeLongerThanBefore()
    {
        var page = _scenarioContext.GetState().Page!;
        var beforeLength = _scenarioContext.GetState().EditorLengthBeforeAction ?? 0;
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (DateTime.UtcNow < deadline)
        {
            var current = await page.Locator("textarea.editor-input").InputValueAsync();
            if (current.Length > beforeLength)
            {
                return;
            }

            await page.WaitForTimeoutAsync(300);
        }

        throw new AssertionException("Editor content did not grow after AI continue.");
    }

    [When("I refresh insights from the toolbar")]
    public async Task WhenIRefreshInsightsFromTheToolbar()
    {
        var page = _scenarioContext.GetState().Page!;
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh" }).ClickAsync();
    }

    [Then(@"plugin panel ""(.*)"" should be visible")]
    public async Task ThenPluginPanelShouldBeVisible(string panelTitle)
    {
        var page = _scenarioContext.GetState().Page!;
        var panel = page.Locator($"//section[contains(@class,'tool-window')][.//h2[normalize-space()='{panelTitle}']]");

        for (var i = 0; i < 80; i++)
        {
            var panelCount = await panel.CountAsync();
            if (panelCount > 0)
            {
                return;
            }

            await page.WaitForTimeoutAsync(250);
        }

        var pageHtml = await page.ContentAsync();
        var snapshotPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"panel-failure-{SanitizeFileName(panelTitle)}.html");
        File.WriteAllText(snapshotPath, pageHtml);
        var headerCount = await page.Locator($"//section[contains(@class,'tool-window')][.//h2[normalize-space()='{panelTitle}']]").CountAsync();
        throw new AssertionException($"Panel '{panelTitle}' was not visible. PanelCount={headerCount}. Snapshot={snapshotPath}");
    }

    [When("I request API provider summary")]
    public async Task WhenIRequestApiProviderSummary()
    {
        var state = _scenarioContext.GetState();

        using var client = CreateApiClient();
        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);

        var summary = json.RootElement.GetProperty("providerSummary");
        state.ProviderPreference = summary.GetProperty("preference").GetString();
        state.OllamaConfigured = summary.GetProperty("ollamaConfigured").GetBoolean();
        state.OpenAiConfigured = summary.GetProperty("openAiConfigured").GetBoolean();
    }

    [Then(@"provider preference should be ""(.*)""")]
    public void ThenProviderPreferenceShouldBe(string expected)
    {
        Assert.That(_scenarioContext.GetState().ProviderPreference, Is.EqualTo(expected));
    }

    [Then("ollama should be configured")]
    public void ThenOllamaShouldBeConfigured()
    {
        Assert.That(_scenarioContext.GetState().OllamaConfigured, Is.True);
    }

    [When(@"I request continuation for ""(.*)""")]
    public async Task WhenIRequestContinuationFor(string draft)
    {
        var state = _scenarioContext.GetState();

        using var client = CreateApiClient();
        using var response = await client.PostAsJsonAsync("/ai/continue", new { Draft = Normalize(draft) });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ContinueResponse>();
        state.ContinuationOutput = payload?.Continuation;
        state.ContinuationSource = payload?.Source;
    }

    [Then("continuation output should be returned")]
    public void ThenContinuationOutputShouldBeReturned()
    {
        var state = _scenarioContext.GetState();
        Assert.That(state.ContinuationOutput, Is.Not.Null.And.Not.Empty);
        Assert.That(state.ContinuationSource, Is.Not.Null.And.Not.Empty);
    }

    [When(@"I execute slash command ""(.*)"" for ""(.*)""")]
    public async Task WhenIExecuteSlashCommandFor(string command, string draft)
    {
        var state = _scenarioContext.GetState();

        using var client = CreateApiClient();
        using var response = await client.PostAsJsonAsync("/ai/slash", new
        {
            Command = command,
            Draft = Normalize(draft),
            Prompt = (string?)null
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SlashResponse>();
        state.SlashOutput = payload?.Output;
        state.SlashSource = payload?.Source;
    }

    [Then("slash command output should be returned")]
    public void ThenSlashCommandOutputShouldBeReturned()
    {
        var state = _scenarioContext.GetState();
        Assert.That(state.SlashOutput, Is.Not.Null.And.Not.Empty);
        Assert.That(state.SlashSource, Is.Not.Null.And.Not.Empty);
    }

    [When(@"^I request assist stream for mode ""(.*)""$")]
    public async Task WhenIRequestAssistStreamForMode(string mode)
    {
        await RequestAssistStreamAsync(mode, null);
    }

    [When(@"I request assist stream with custom prompt for mode ""(.*)"" and prompt ""(.*)""")]
    public async Task WhenIRequestAssistStreamWithCustomPrompt(string mode, string prompt)
    {
        await RequestAssistStreamAsync(mode, prompt);
    }

    private async Task RequestAssistStreamAsync(string mode, string? prompt)
    {
        var state = _scenarioContext.GetState();
        state.AssistChunks.Clear();

        using var client = CreateApiClient();
        using var response = await client.PostAsJsonAsync("/ai/assist/stream", new
        {
            Mode = mode,
            Draft = "# Stream test\n\n- collect assistant items",
            Prompt = prompt
        });
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var json = JsonDocument.Parse(line);
            JsonElement textProperty;
            if (json.RootElement.TryGetProperty("Text", out textProperty) ||
                json.RootElement.TryGetProperty("text", out textProperty))
            {
                var text = textProperty.ValueKind == JsonValueKind.String ? textProperty.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    state.AssistChunks.Add(text);
                }
            }

            if (state.AssistChunks.Count >= 8)
            {
                break;
            }
        }
    }

    [Then(@"assist stream should return at least (\d+) chunk")]
    public void ThenAssistStreamShouldReturnAtLeastChunk(int minimum)
    {
        var count = _scenarioContext.GetState().AssistChunks.Count;
        Assert.That(count, Is.GreaterThanOrEqualTo(minimum));
    }

    [Then("local state should contain note layers with diff and tone")]
    public async Task ThenLocalStateShouldContainNoteLayersWithDiffAndTone()
    {
        var page = _scenarioContext.GetState().Page!;

        var payload = await page.EvaluateAsync<string>(@"
            (() => {
                const raw = localStorage.getItem('jdwriter.state.v1');
                if (!raw) return '';
                const state = JSON.parse(raw);
                if (!state || !Array.isArray(state.notes) || state.notes.length === 0) return '';
                const note = state.notes.find(n => n && Array.isArray(n.layers) && n.layers.length > 0) || state.notes[0];
                if (!note || !Array.isArray(note.layers) || note.layers.length === 0) return '';
                const latest = note.layers[note.layers.length - 1];
                return JSON.stringify(latest);
            })();
        ");

        Assert.That(payload, Is.Not.Null.And.Not.Empty, "Expected persisted layer payload.");
        using var json = JsonDocument.Parse(payload);

        Assert.That(json.RootElement.TryGetProperty("operation", out _), Is.True, "Layer missing operation.");
        Assert.That(json.RootElement.TryGetProperty("source", out _), Is.True, "Layer missing source.");
        Assert.That(json.RootElement.TryGetProperty("diff", out var diff), Is.True, "Layer missing diff.");
        Assert.That(json.RootElement.TryGetProperty("tone", out var tone), Is.True, "Layer missing tone.");
        Assert.That(diff.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(tone.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Then("local state should include voice transcript and cleanup operations")]
    public async Task ThenLocalStateShouldIncludeVoiceTranscriptAndCleanupOperations()
    {
        var page = _scenarioContext.GetState().Page!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> lastOperations = [];

        while (DateTime.UtcNow < deadline)
        {
            var payload = await page.EvaluateAsync<string>(@"
                (() => {
                    const raw = localStorage.getItem('jdwriter.state.v1');
                    if (!raw) return '';
                    const state = JSON.parse(raw);
                    if (!state || !Array.isArray(state.notes) || state.notes.length === 0) return '';
                    const activeNote = state.notes.find(n => n.id === state.activeNoteId) || state.notes[0];
                    if (!activeNote || !Array.isArray(activeNote.layers)) return '';
                    const operations = activeNote.layers.map(l => l.operation || '');
                    return JSON.stringify(operations);
                })();
            ");

            var operations = string.IsNullOrWhiteSpace(payload)
                ? []
                : JsonSerializer.Deserialize<List<string>>(payload) ?? [];
            lastOperations = operations;

            var hasTranscript = operations.Contains("voice-transcript");
            var hasCleanup = operations.Any(op => op.StartsWith("voice-cleanup", StringComparison.OrdinalIgnoreCase));
            if (hasTranscript && hasCleanup)
            {
                return;
            }

            await page.WaitForTimeoutAsync(250);
        }

        var operationText = lastOperations.Count == 0
            ? "(none)"
            : string.Join(", ", lastOperations);
        throw new AssertionException($"Expected voice-transcript and voice-cleanup layer operations in local state. Operations: {operationText}");
    }

    [Then("local state should include voice session transcript events")]
    public async Task ThenLocalStateShouldIncludeVoiceSessionTranscriptEvents()
    {
        var page = _scenarioContext.GetState().Page!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastPayload = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var payload = await page.EvaluateAsync<string>(@"
                (() => {
                    const raw = localStorage.getItem('jdwriter.state.v1');
                    if (!raw) return '';
                    const state = JSON.parse(raw);
                    if (!state || !Array.isArray(state.notes) || state.notes.length === 0) return '';
                    const activeNote = state.notes.find(n => n.id === state.activeNoteId) || state.notes[0];
                    if (!activeNote || !Array.isArray(activeNote.voiceSessions) || activeNote.voiceSessions.length === 0) return '';
                    const latest = activeNote.voiceSessions[activeNote.voiceSessions.length - 1];
                    if (!latest || !Array.isArray(latest.events)) return '';
                    return JSON.stringify(latest.events);
                })();
            ");

            lastPayload = payload;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var events = json.RootElement.EnumerateArray().ToList();
                    var hasInterim = events.Any(evt =>
                        evt.TryGetProperty("kind", out var kind) &&
                        string.Equals(kind.GetString(), "transcript-interim", StringComparison.OrdinalIgnoreCase));
                    var hasFinal = events.Any(evt =>
                        evt.TryGetProperty("kind", out var kind) &&
                        string.Equals(kind.GetString(), "transcript-finalized", StringComparison.OrdinalIgnoreCase));
                    var hasInserted = events.Any(evt =>
                        evt.TryGetProperty("kind", out var kind) &&
                        string.Equals(kind.GetString(), "transcript-inserted", StringComparison.OrdinalIgnoreCase));

                    if (hasInterim && hasFinal && hasInserted)
                    {
                        return;
                    }
                }
            }

            await page.WaitForTimeoutAsync(200);
        }

        throw new AssertionException($"Expected persisted voice session events (interim/finalized/inserted). Payload: {lastPayload}");
    }

    [Then(@"voice review panel should contain ""(.*)""")]
    public async Task ThenVoiceReviewPanelShouldContain(string text)
    {
        var page = _scenarioContext.GetState().Page!;
        var normalized = Normalize(text);
        var reviewPanel = page.Locator("//section[contains(@class,'tool-window')][.//h2[normalize-space()='Voice Review']]");
        await Assertions.Expect(reviewPanel).ToBeVisibleAsync();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var panelText = await reviewPanel.First.InnerTextAsync();
            if (panelText.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await page.WaitForTimeoutAsync(200);
        }

        var latestPanelText = await reviewPanel.First.InnerTextAsync();
        throw new AssertionException($"Voice Review panel did not contain expected text '{normalized}'. Actual panel text: {latestPanelText}");
    }

    [Then(@"app background variable should be ""(.*)""")]
    public async Task ThenAppBackgroundVariableShouldBe(string expected)
    {
        var page = _scenarioContext.GetState().Page!;
        var actual = await page.EvaluateAsync<string>(@"
            (() => getComputedStyle(document.documentElement).getPropertyValue('--app-bg').trim())();
        ");

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static string Normalize(string value)
    {
        return value.Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static HttpClient CreateApiClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri(TestHostManager.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private static async Task EnsureEditorReadyAsync(IPage page)
    {
        var editor = page.Locator("textarea.editor-input");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await editor.CountAsync() > 0)
            {
                await editor.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 5000
                });
                return;
            }

            var createButton = page.Locator(".empty-state .solid-button");
            if (await createButton.CountAsync() > 0)
            {
                await createButton.First.ClickAsync();
            }

            await page.WaitForTimeoutAsync(300);
        }

        var outputDir = TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(outputDir);
        var htmlPath = Path.Combine(outputDir, "studio-failure.html");
        var screenshotPath = Path.Combine(outputDir, "studio-failure.png");
        File.WriteAllText(htmlPath, await page.ContentAsync());
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true });

        throw new AssertionException("Editor textarea did not become ready.");
    }

    private static async Task WaitForEditorToContainAsync(IPage page, string expected, TimeSpan timeout)
    {
        var editor = page.Locator("textarea.editor-input");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var current = await editor.InputValueAsync();
            if (current.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await page.WaitForTimeoutAsync(200);
        }

        var latest = await editor.InputValueAsync();
        throw new AssertionException($"Editor did not contain expected text. Expected '{expected}', actual '{latest}'.");
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(input.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Replace(' ', '-');
    }

    private sealed record ContinueResponse(string Continuation, string Source);
    private sealed record SlashResponse(string Output, string Source);
}

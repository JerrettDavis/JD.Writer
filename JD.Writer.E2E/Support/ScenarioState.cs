using Microsoft.Playwright;
using System.Text.Json;

namespace JD.Writer.E2E.Support;

internal sealed class ScenarioState
{
    public IPlaywright? Playwright { get; set; }
    public IBrowser? Browser { get; set; }
    public IBrowserContext? BrowserContext { get; set; }
    public IPage? Page { get; set; }

    public string? ContinuationOutput { get; set; }
    public string? ContinuationSource { get; set; }
    public string? SlashOutput { get; set; }
    public string? SlashSource { get; set; }
    public List<string> AssistChunks { get; } = [];
    public string? ProviderPreference { get; set; }
    public bool? OllamaConfigured { get; set; }
    public bool? OpenAiConfigured { get; set; }
    public int? EditorLengthBeforeAction { get; set; }
    public string WebBaseUrl { get; set; } = TestHostManager.WebBaseUrl;
}

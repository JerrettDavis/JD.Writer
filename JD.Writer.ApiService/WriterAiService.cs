using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace JD.Writer.ApiService;

public sealed class WriterAiService
{
    private readonly ILogger<WriterAiService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IChatCompletionService? _chatCompletion;
    private readonly Kernel? _kernel;

    private readonly string _providerPreference;
    private readonly string? _ollamaModel;
    private readonly string _ollamaEndpoint;
    private readonly TimeSpan _ollamaTimeout;

    public WriterAiService(
        IConfiguration configuration,
        ILogger<WriterAiService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _providerPreference = configuration["AI:Provider"]?.Trim().ToLowerInvariant() ?? "auto";
        _ollamaModel = configuration["AI:Ollama:Model"];
        _ollamaEndpoint = configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434";

        var timeoutSeconds = 45;
        if (int.TryParse(configuration["AI:Ollama:TimeoutSeconds"], out var configuredSeconds) && configuredSeconds > 0)
        {
            timeoutSeconds = configuredSeconds;
        }
        _ollamaTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        var openAiApiKey = configuration["AI:OpenAI:ApiKey"] ?? configuration["OPENAI_API_KEY"];
        var openAiModel = configuration["AI:OpenAI:Model"] ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            return;
        }

        try
        {
            var kernelBuilder = Kernel.CreateBuilder();
            kernelBuilder.AddOpenAIChatCompletion(modelId: openAiModel, apiKey: openAiApiKey);
            _kernel = kernelBuilder.Build();
            _chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Semantic Kernel initialization failed. Falling back to configured providers.");
        }
    }

    public static object GetProviderSummary(IConfiguration configuration)
    {
        var openAiApiKey = configuration["AI:OpenAI:ApiKey"] ?? configuration["OPENAI_API_KEY"];
        var provider = configuration["AI:Provider"] ?? "auto";
        var ollamaModel = configuration["AI:Ollama:Model"];
        var ollamaEndpoint = configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434";

        return new
        {
            preference = provider,
            openAiConfigured = !string.IsNullOrWhiteSpace(openAiApiKey),
            ollamaConfigured = !string.IsNullOrWhiteSpace(ollamaModel),
            ollamaEndpoint,
            ollamaModel = string.IsNullOrWhiteSpace(ollamaModel) ? null : ollamaModel
        };
    }

    public async Task<ContinueDraftResponse> ContinueDraftAsync(string draft, CancellationToken cancellationToken)
    {
        var promptWindow = TrimForPrompt(draft, 6000);

        var systemPrompt = "You are JD.Writer, a focused markdown writing copilot. Continue the user's draft in the same tone. " +
                           "Return only markdown content to append, no preamble.";
        var userPrompt = $"Continue this draft:\n\n{promptWindow}";

        var generated = await GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);
        if (!string.IsNullOrWhiteSpace(generated.Text))
        {
            return new ContinueDraftResponse(generated.Text.Trim(), generated.Source);
        }

        return new ContinueDraftResponse(BuildFallbackContinuation(promptWindow), "fallback");
    }

    public async IAsyncEnumerable<AssistStreamChunk> StreamAssistAsync(
        string mode,
        string draft,
        string? prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "hints" : mode.Trim().ToLowerInvariant();
        var promptWindow = TrimForPrompt(draft, 3500);

        var modeDirective = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : normalizedMode switch
            {
                "help" => "Give practical markdown editing help and next actions.",
                "brainstorm" => "Generate creative but specific idea sparks tied to the draft.",
                _ => "Give concise quality hints to improve clarity and structure."
            };

        var systemPrompt = "You are JD.Writer's side panel assistant. Return short lines only, one item per line, max 12 words each, and no numbering.";
        var userPrompt = $"{modeDirective}\n\nDraft:\n{promptWindow}";

        var generated = await GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);
        var lines = string.IsNullOrWhiteSpace(generated.Text)
            ? []
            : generated.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(TrimListPrefix)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var source = generated.Source;

        if (lines.Count == 0)
        {
            lines = BuildFallbackAssistLines(normalizedMode, promptWindow);
            source = "fallback";
        }

        foreach (var line in lines.Take(8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return new AssistStreamChunk(line, source);
            await Task.Delay(180, cancellationToken);
        }
    }

    public async Task<SlashCommandResponse> RunSlashCommandAsync(
        string command,
        string draft,
        string? prompt,
        CancellationToken cancellationToken)
    {
        var normalizedCommand = string.IsNullOrWhiteSpace(command) ? "rewrite" : command.Trim().ToLowerInvariant();
        var promptWindow = TrimForPrompt(draft, 6000);

        var directive = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : normalizedCommand switch
            {
                "summarize" => "Summarize this draft as concise markdown bullets with headings if useful.",
                "outline" => "Convert this draft into a clear markdown outline with logical sections.",
                "action-items" => "Extract concrete action items with owners and next steps where possible.",
                _ => $"Apply slash command '{normalizedCommand}' to improve this draft while preserving intent."
            };

        var systemPrompt = "You are JD.Writer slash command runtime. Return markdown only with no preamble.";
        var userPrompt = $"{directive}\n\nDraft:\n{promptWindow}";

        var generated = await GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);
        if (!string.IsNullOrWhiteSpace(generated.Text))
        {
            return new SlashCommandResponse(generated.Text.Trim(), generated.Source);
        }

        return new SlashCommandResponse(BuildFallbackSlashOutput(normalizedCommand, promptWindow), "fallback");
    }

    private async Task<GeneratedText> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        foreach (var provider in GetProviderOrder())
        {
            if (provider == "ollama")
            {
                var ollamaText = await TryGenerateWithOllamaAsync(systemPrompt, userPrompt, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ollamaText))
                {
                    return new GeneratedText(ollamaText, "ollama");
                }
            }

            if (provider == "openai")
            {
                var openAiText = await TryGenerateWithSemanticKernelAsync(systemPrompt, userPrompt, cancellationToken);
                if (!string.IsNullOrWhiteSpace(openAiText))
                {
                    return new GeneratedText(openAiText, "semantic-kernel");
                }
            }
        }

        return new GeneratedText(null, "fallback");
    }

    private IEnumerable<string> GetProviderOrder()
    {
        return _providerPreference switch
        {
            "openai" => ["openai", "ollama"],
            "ollama" => ["ollama", "openai"],
            "fallback" => [],
            _ => ["ollama", "openai"]
        };
    }

    private async Task<string?> TryGenerateWithSemanticKernelAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (_chatCompletion is null || _kernel is null)
        {
            return null;
        }

        try
        {
            var history = new ChatHistory(systemPrompt);
            history.AddUserMessage(userPrompt);

            var response = await _chatCompletion.GetChatMessageContentAsync(
                history,
                kernel: _kernel,
                cancellationToken: cancellationToken);

            return response.Content?.Trim();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Semantic Kernel call failed.");
            return null;
        }
    }

    private async Task<string?> TryGenerateWithOllamaAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ollamaModel))
        {
            return null;
        }

        var endpoint = _ollamaEndpoint.TrimEnd('/');
        var request = new
        {
            model = _ollamaModel,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = _ollamaTimeout;

            using var response = await client.PostAsJsonAsync(
                endpoint + "/api/chat",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama call returned status code {StatusCode}.", response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString()?.Trim();
            }

            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Ollama call failed.");
            return null;
        }
    }

    private static List<string> BuildFallbackAssistLines(string mode, string draft)
    {
        var lineCount = draft.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var words = draft.Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var wordCount = words.Length;
        var hasHeading = draft.Contains("# ", StringComparison.Ordinal);

        return mode switch
        {
            "help" =>
            [
                "Use #, ##, ### headings to keep sections scannable.",
                "Use fenced code blocks for commands and snippets.",
                "Capture decisions as checklist items to track execution.",
                "Keep paragraphs to 3-4 lines for readability.",
                "Hotkey: Ctrl+J continues your draft with AI."
            ],
            "brainstorm" =>
            [
                "Add a friction log: what slows writing most today?",
                "Draft a tiny plugin API for custom transforms.",
                "Sketch voice memo workflow from capture to markdown.",
                "Define offline sync conflict strategy in one paragraph.",
                "Invent three note templates: sprint, meeting, research."
            ],
            _ =>
            [
                hasHeading ? "Strong heading structure detected; add one summary section." : "Start with one clear heading for this draft.",
                wordCount < 80 ? "Expand with one concrete example to ground the idea." : "Trim repeated phrases to tighten flow.",
                lineCount < 5 ? "Add bullet checkpoints so this becomes actionable." : "Group related lines under subheadings.",
                "End with a next action sentence to preserve momentum.",
                "Mark unknowns with TODO tags for rapid follow-up."
            ]
        };
    }

    private static string BuildFallbackContinuation(string draft)
    {
        var lastLine = draft
            .Split('\n', StringSplitOptions.TrimEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? "the current draft";

        return $"## Next Move\n\nBuild on \"{lastLine}\" by converting it into 3 concrete actions:\n\n- Define the smallest usable workflow and test it end-to-end.\n- Capture one measurable success metric for this pass.\n- List one risk and one mitigation before implementation.\n";
    }

    private static string BuildFallbackSlashOutput(string command, string draft)
    {
        return command switch
        {
            "summarize" => "## Summary\n\n- Core idea captured.\n- Main next steps identified.\n- Add one measurable success metric.",
            "outline" => "## Outline\n\n### Context\n- \n\n### Approach\n- \n\n### Next Actions\n- ",
            "action-items" => "## Action Items\n\n- [ ] Define owner for each task.\n- [ ] Add due dates for execution steps.\n- [ ] Track one risk per task.",
            _ => $"## /{command}\n\nReview this section and tighten wording for clarity:\n\n{TrimForPrompt(draft, 320)}"
        };
    }

    private static string TrimForPrompt(string draft, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(draft))
        {
            return string.Empty;
        }

        return draft.Length <= maxLength ? draft : draft[^maxLength..];
    }

    private static string TrimListPrefix(string line)
    {
        return line.Trim().TrimStart('-', '*', '.', '•', ' ');
    }

    private sealed record GeneratedText(string? Text, string Source);
}

public sealed record ContinueDraftRequest(string Draft);
public sealed record ContinueDraftResponse(string Continuation, string Source);
public sealed record AssistStreamRequest(string Mode, string Draft, string? Prompt);
public sealed record AssistStreamChunk(string Text, string Source);
public sealed record SlashCommandRequest(string Command, string Draft, string? Prompt);
public sealed record SlashCommandResponse(string Output, string Source);

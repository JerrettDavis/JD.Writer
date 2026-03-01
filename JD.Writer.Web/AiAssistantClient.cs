using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace JD.Writer.Web;

public sealed class AiAssistantClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<AiAssistantClient> _logger;
    private readonly string _mode;

    public AiAssistantClient(HttpClient httpClient, ILogger<AiAssistantClient> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;

        _mode = NormalizeMode(configuration["AiClient:Mode"]);
    }

    public async Task<ContinueDraftResponse> ContinueDraftAsync(ContinueDraftRequest request, CancellationToken cancellationToken = default)
    {
        var draft = request.Draft ?? string.Empty;
        if (UseLocalOnly())
        {
            return BuildLocalContinuationResponse(draft, source: "local");
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/ai/continue", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<ContinueDraftResponse>(JsonOptions, cancellationToken);
            if (payload is not null)
            {
                return payload;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            _logger.LogDebug(exception, "Remote continuation failed. Falling back to local continuation.");
        }

        return BuildLocalContinuationResponse(draft, source: "fallback-local");
    }

    public async IAsyncEnumerable<AssistStreamChunk> StreamAssistAsync(
        string mode,
        string draft,
        string? prompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (UseLocalOnly())
        {
            foreach (var chunk in BuildLocalAssistChunks(mode, draft, prompt, "local"))
            {
                yield return chunk;
                await Task.Delay(140, cancellationToken);
            }

            yield break;
        }

        var remoteChunks = await TryReadRemoteStreamAsync(mode, draft, prompt, cancellationToken);
        if (remoteChunks.Count > 0)
        {
            foreach (var chunk in remoteChunks)
            {
                yield return chunk;
            }

            yield break;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        foreach (var chunk in BuildLocalAssistChunks(mode, draft, prompt, "fallback-local"))
        {
            yield return chunk;
            await Task.Delay(140, cancellationToken);
        }
    }

    public async Task<SlashCommandResponse> RunSlashCommandAsync(
        SlashCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = request.Draft ?? string.Empty;
        if (UseLocalOnly())
        {
            return BuildLocalSlashResponse(request.Command, draft, source: "local");
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/ai/slash", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<SlashCommandResponse>(JsonOptions, cancellationToken);
            if (payload is not null)
            {
                return payload;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            _logger.LogDebug(exception, "Remote slash command failed. Falling back to local transform.");
        }

        return BuildLocalSlashResponse(request.Command, draft, source: "fallback-local");
    }

    private static string NormalizeMode(string? configuredMode)
    {
        var normalized = configuredMode?.Trim().ToLowerInvariant();
        return normalized is "local" or "remote" ? normalized : "auto";
    }

    private bool UseLocalOnly() => string.Equals(_mode, "local", StringComparison.Ordinal);

    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException;
    }

    private async Task<List<AssistStreamChunk>> TryReadRemoteStreamAsync(
        string mode,
        string draft,
        string? prompt,
        CancellationToken cancellationToken)
    {
        var chunks = new List<AssistStreamChunk>(8);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/ai/assist/stream",
                new AssistStreamRequest(mode, draft, prompt),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var chunk = JsonSerializer.Deserialize<AssistStreamChunk>(line, JsonOptions);
                    if (chunk is not null)
                    {
                        chunks.Add(chunk);
                    }
                }
                catch (JsonException exception)
                {
                    _logger.LogDebug(exception, "Skipping malformed stream payload.");
                }
            }
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            _logger.LogDebug(exception, "Remote stream failed. Falling back to local stream hints.");
        }

        return chunks;
    }

    private static ContinueDraftResponse BuildLocalContinuationResponse(string draft, string source)
    {
        var trimmed = TrimForPrompt(draft, 6000);
        var lastLine = trimmed
            .Split('\n', StringSplitOptions.TrimEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? "the current draft";

        var continuation = $"## Next Move\n\nBuild on \"{lastLine}\" by converting it into 3 concrete actions:\n\n- Define the smallest usable workflow and test it end-to-end.\n- Capture one measurable success metric for this pass.\n- List one risk and one mitigation before implementation.\n";

        return new ContinueDraftResponse(continuation, source);
    }

    private static SlashCommandResponse BuildLocalSlashResponse(string? command, string draft, string source)
    {
        var normalized = string.IsNullOrWhiteSpace(command) ? "rewrite" : command.Trim().ToLowerInvariant();
        var trimmedDraft = TrimForPrompt(draft, 360);

        var output = normalized switch
        {
            "summarize" => "## Summary\n\n- Core idea captured.\n- Main next steps identified.\n- Add one measurable success metric.",
            "outline" => "## Outline\n\n### Context\n- \n\n### Approach\n- \n\n### Next Actions\n- ",
            "action-items" => "## Action Items\n\n- [ ] Define owner for each task.\n- [ ] Add due dates for execution steps.\n- [ ] Track one risk per task.",
            _ => $"## /{normalized}\n\nReview this section and tighten wording for clarity:\n\n{trimmedDraft}"
        };

        return new SlashCommandResponse(output, source);
    }

    private static List<AssistStreamChunk> BuildLocalAssistChunks(string mode, string draft, string? prompt, string source)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "hints" : mode.Trim().ToLowerInvariant();
        var normalizedPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();

        var lines = BuildFallbackAssistLines(normalizedMode, TrimForPrompt(draft, 3600));

        if (!string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            lines.Insert(0, $"Prompt focus: {normalizedPrompt}");
        }

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(line => new AssistStreamChunk(line, source))
            .ToList();
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

    private static string TrimForPrompt(string draft, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(draft))
        {
            return string.Empty;
        }

        return draft.Length <= maxLength ? draft : draft[^maxLength..];
    }
}

public sealed record ContinueDraftRequest(string Draft);
public sealed record ContinueDraftResponse(string Continuation, string Source);
public sealed record AssistStreamRequest(string Mode, string Draft, string? Prompt);
public sealed record AssistStreamChunk(string Text, string Source);
public sealed record SlashCommandRequest(string Command, string Draft, string? Prompt);
public sealed record SlashCommandResponse(string Output, string Source);

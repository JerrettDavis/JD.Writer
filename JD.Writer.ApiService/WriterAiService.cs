using System.Net.Http.Json;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly bool _nativeLlamaEnabled;
    private readonly string? _nativeLlamaCliPath;
    private readonly string? _nativeLlamaModelDirectory;
    private readonly string? _nativeLlamaGpuModelPath;
    private readonly string? _nativeLlamaCpuModelPath;
    private readonly int _nativeLlamaContextSize;
    private readonly int _nativeLlamaMaxTokens;
    private readonly int _nativeLlamaGpuLayers;
    private readonly int _nativeLlamaThreads;
    private readonly double _nativeLlamaTemperature;
    private readonly TimeSpan _nativeLlamaTimeout;
    private readonly HardwareProfile _hardwareProfile;

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

        _nativeLlamaEnabled = ParseBool(configuration["AI:NativeLlama:Enabled"], defaultValue: true);
        _nativeLlamaModelDirectory = NormalizeConfiguredPath(configuration["AI:NativeLlama:ModelDirectory"]);
        _nativeLlamaCliPath = ResolveNativeLlamaCliPath(configuration["AI:NativeLlama:CliPath"]);
        _nativeLlamaGpuModelPath = ResolveNativeModelPath(
            configuredPath: configuration["AI:NativeLlama:GpuModelPath"],
            modelDirectory: _nativeLlamaModelDirectory,
            preferGpu: true);
        _nativeLlamaCpuModelPath = ResolveNativeModelPath(
            configuredPath: configuration["AI:NativeLlama:CpuModelPath"],
            modelDirectory: _nativeLlamaModelDirectory,
            preferGpu: false);
        _nativeLlamaContextSize = ParseInt(configuration["AI:NativeLlama:ContextSize"], defaultValue: 4096, minValue: 512, maxValue: 32768);
        _nativeLlamaMaxTokens = ParseInt(configuration["AI:NativeLlama:MaxTokens"], defaultValue: 280, minValue: 32, maxValue: 4096);
        _nativeLlamaGpuLayers = ParseInt(configuration["AI:NativeLlama:GpuLayers"], defaultValue: 99, minValue: 0, maxValue: 200);
        _nativeLlamaThreads = ParseInt(configuration["AI:NativeLlama:Threads"], defaultValue: 0, minValue: 0, maxValue: 128);
        _nativeLlamaTemperature = ParseDouble(configuration["AI:NativeLlama:Temperature"], defaultValue: 0.35d, minValue: 0d, maxValue: 2d);
        var nativeTimeoutSeconds = ParseInt(configuration["AI:NativeLlama:TimeoutSeconds"], defaultValue: 90, minValue: 10, maxValue: 600);
        _nativeLlamaTimeout = TimeSpan.FromSeconds(nativeTimeoutSeconds);
        _hardwareProfile = DetectHardwareProfile();

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
        var nativeEnabled = ParseBool(configuration["AI:NativeLlama:Enabled"], defaultValue: true);
        var nativeModelDirectory = NormalizeConfiguredPath(configuration["AI:NativeLlama:ModelDirectory"]);
        var nativeCliPath = ResolveNativeLlamaCliPath(configuration["AI:NativeLlama:CliPath"]);
        var nativeGpuModelPath = ResolveNativeModelPath(
            configuredPath: configuration["AI:NativeLlama:GpuModelPath"],
            modelDirectory: nativeModelDirectory,
            preferGpu: true);
        var nativeCpuModelPath = ResolveNativeModelPath(
            configuredPath: configuration["AI:NativeLlama:CpuModelPath"],
            modelDirectory: nativeModelDirectory,
            preferGpu: false);

        return new
        {
            preference = provider,
            openAiConfigured = !string.IsNullOrWhiteSpace(openAiApiKey),
            ollamaConfigured = !string.IsNullOrWhiteSpace(ollamaModel),
            ollamaEndpoint,
            ollamaModel = string.IsNullOrWhiteSpace(ollamaModel) ? null : ollamaModel,
            nativeLlamaEnabled = nativeEnabled,
            nativeLlamaModelDirectory = nativeModelDirectory,
            nativeLlamaCliPath = nativeCliPath,
            nativeLlamaGpuModelPath = nativeGpuModelPath,
            nativeLlamaCpuModelPath = nativeCpuModelPath,
            nativeLlamaConfigured = nativeEnabled && !string.IsNullOrWhiteSpace(nativeCliPath) &&
                (!string.IsNullOrWhiteSpace(nativeGpuModelPath) || !string.IsNullOrWhiteSpace(nativeCpuModelPath))
        };
    }

    public object GetRuntimeProviderSummary()
    {
        var orderedProviders = GetProviderOrder().ToArray();
        var nativeAvailable = IsNativeLlamaAvailable();

        return new
        {
            preference = _providerPreference,
            openAiConfigured = _chatCompletion is not null,
            ollamaConfigured = !string.IsNullOrWhiteSpace(_ollamaModel),
            ollamaEndpoint = _ollamaEndpoint,
            ollamaModel = _ollamaModel,
            nativeLlamaEnabled = _nativeLlamaEnabled,
            nativeLlamaConfigured = nativeAvailable,
            nativeLlamaModelDirectory = _nativeLlamaModelDirectory,
            nativeLlamaCliPath = _nativeLlamaCliPath,
            nativeLlamaGpuModelPath = _nativeLlamaGpuModelPath,
            nativeLlamaCpuModelPath = _nativeLlamaCpuModelPath,
            hardware = new
            {
                _hardwareProfile.HasSupportedGpu,
                _hardwareProfile.PreferredAcceleration,
                _hardwareProfile.GpuNames,
                _hardwareProfile.Notes
            },
            providerOrder = orderedProviders
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

            if (provider == "native-llama-gpu")
            {
                var nativeGpuText = await TryGenerateWithNativeLlamaAsync(systemPrompt, userPrompt, preferGpu: true, cancellationToken);
                if (!string.IsNullOrWhiteSpace(nativeGpuText))
                {
                    return new GeneratedText(nativeGpuText, "native-llama-gpu");
                }
            }

            if (provider == "native-llama-cpu")
            {
                var nativeCpuText = await TryGenerateWithNativeLlamaAsync(systemPrompt, userPrompt, preferGpu: false, cancellationToken);
                if (!string.IsNullOrWhiteSpace(nativeCpuText))
                {
                    return new GeneratedText(nativeCpuText, "native-llama-cpu");
                }
            }
        }

        return new GeneratedText(null, "fallback");
    }

    private IEnumerable<string> GetProviderOrder()
    {
        return _providerPreference switch
        {
            "openai" => ["openai", "ollama", "native-llama-gpu", "native-llama-cpu"],
            "ollama" => ["ollama", "native-llama-gpu", "native-llama-cpu", "openai"],
            "native" => ["native-llama-gpu", "native-llama-cpu", "ollama", "openai"],
            "local" => ["native-llama-gpu", "native-llama-cpu", "ollama"],
            "fallback" => [],
            _ => ["ollama", "native-llama-gpu", "native-llama-cpu", "openai"]
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

    private async Task<string?> TryGenerateWithNativeLlamaAsync(
        string systemPrompt,
        string userPrompt,
        bool preferGpu,
        CancellationToken cancellationToken)
    {
        if (!IsNativeLlamaAvailable())
        {
            return null;
        }

        if (preferGpu && !_hardwareProfile.HasSupportedGpu)
        {
            return null;
        }

        var modelPath = ResolveNativeLlamaModelPath(preferGpu);
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var prompt = BuildNativeLlamaPrompt(systemPrompt, userPrompt);
        var args = new List<string>
        {
            "-m",
            modelPath,
            "-p",
            prompt,
            "-n",
            _nativeLlamaMaxTokens.ToString(CultureInfo.InvariantCulture),
            "-c",
            _nativeLlamaContextSize.ToString(CultureInfo.InvariantCulture),
            "--temp",
            _nativeLlamaTemperature.ToString("0.###", CultureInfo.InvariantCulture),
            "--n-gpu-layers",
            (preferGpu ? _nativeLlamaGpuLayers : 0).ToString(CultureInfo.InvariantCulture)
        };

        if (_nativeLlamaThreads > 0)
        {
            args.Add("-t");
            args.Add(_nativeLlamaThreads.ToString(CultureInfo.InvariantCulture));
        }

        var runResult = await RunProcessAsync(_nativeLlamaCliPath!, args, _nativeLlamaTimeout, cancellationToken);
        if (!runResult.Success)
        {
            if (runResult.TimedOut)
            {
                _logger.LogWarning(
                    "Native llama provider timed out after {TimeoutSeconds}s (preferGpu={PreferGpu}).",
                    _nativeLlamaTimeout.TotalSeconds,
                    preferGpu);
            }
            else
            {
                _logger.LogWarning(
                    "Native llama provider failed with exit code {ExitCode} (preferGpu={PreferGpu}): {Error}",
                    runResult.ExitCode,
                    preferGpu,
                    string.IsNullOrWhiteSpace(runResult.StandardError) ? "(no stderr)" : runResult.StandardError);
            }

            return null;
        }

        var normalized = NormalizeNativeLlamaOutput(runResult.StandardOutput);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private bool IsNativeLlamaAvailable()
    {
        if (!_nativeLlamaEnabled || string.IsNullOrWhiteSpace(_nativeLlamaCliPath))
        {
            return false;
        }

        if (!File.Exists(_nativeLlamaCliPath))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ResolveNativeLlamaModelPath(preferGpu: true)) ||
               !string.IsNullOrWhiteSpace(ResolveNativeLlamaModelPath(preferGpu: false));
    }

    private string? ResolveNativeLlamaModelPath(bool preferGpu)
    {
        if (preferGpu)
        {
            if (PathExists(_nativeLlamaGpuModelPath))
            {
                return _nativeLlamaGpuModelPath;
            }

            if (PathExists(_nativeLlamaCpuModelPath))
            {
                return _nativeLlamaCpuModelPath;
            }

            return null;
        }

        if (PathExists(_nativeLlamaCpuModelPath))
        {
            return _nativeLlamaCpuModelPath;
        }

        return PathExists(_nativeLlamaGpuModelPath) ? _nativeLlamaGpuModelPath : null;
    }

    private static string? ResolveNativeLlamaCliPath(string? configuredCliPath)
    {
        var configured = NormalizeConfiguredPath(configuredCliPath);
        if (PathExists(configured))
        {
            return configured;
        }

        var candidateNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "llama-cli.exe", "main.exe", "llamafile.exe" }
            : new[] { "llama-cli", "main", "llamafile" };

        foreach (var candidate in candidateNames)
        {
            var resolved = TryResolveExecutableOnPath(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveNativeModelPath(string? configuredPath, string? modelDirectory, bool preferGpu)
    {
        var configured = NormalizeConfiguredPath(configuredPath);
        if (PathExists(configured))
        {
            return configured;
        }

        var orderedPatterns = preferGpu
            ? new[] { "*gpu*.gguf", "*cuda*.gguf", "*.gguf" }
            : new[] { "*cpu*.gguf", "*q4*.gguf", "*.gguf" };

        foreach (var directory in GetNativeModelSearchDirectories(modelDirectory))
        {
            var resolved = TryResolveModelInDirectory(directory, orderedPatterns);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetNativeModelSearchDirectories(string? configuredModelDirectory)
    {
        var directories = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredModelDirectory))
        {
            directories.Add(configuredModelDirectory);
        }

        directories.Add(Path.Combine(AppContext.BaseDirectory, "models"));
        directories.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            directories.Add(Path.Combine(localAppData, "JD.Writer", "models"));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            directories.Add(Path.Combine(userProfile, ".jdwriter", "models"));
        }

        return directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeConfiguredPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? TryResolveModelInDirectory(string directory, IReadOnlyList<string> orderedPatterns)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var pattern in orderedPatterns)
        {
            try
            {
                var match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
            catch
            {
                // Ignore inaccessible or invalid directory patterns.
            }
        }

        return null;
    }

    private static string? TryResolveExecutableOnPath(string executableName)
    {
        var rawPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        foreach (var segment in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed path segments.
            }
        }

        return null;
    }

    private static string BuildNativeLlamaPrompt(string systemPrompt, string userPrompt)
    {
        return $"<|system|>\n{systemPrompt}\n<|user|>\n{userPrompt}\n<|assistant|>\n";
    }

    private static string NormalizeNativeLlamaOutput(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return string.Empty;
        }

        var cleaned = rawOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("<|assistant|>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var lines = cleaned
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                !line.StartsWith("main:", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("llama_model_load:", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("print_info:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return string.Join('\n', lines).Trim();
    }

    private static bool PathExists(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string? NormalizeConfiguredPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(rawPath.Trim());
        }
        catch
        {
            return rawPath.Trim();
        }
    }

    private static bool ParseBool(string? raw, bool defaultValue)
    {
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static int ParseInt(string? raw, int defaultValue, int minValue, int maxValue)
    {
        if (!int.TryParse(raw, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, minValue, maxValue);
    }

    private static double ParseDouble(string? raw, double defaultValue, double minValue, double maxValue)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, minValue, maxValue);
    }

    private static HardwareProfile DetectHardwareProfile()
    {
        var gpuNames = new List<string>();
        var notes = new List<string>();

        if (TryCaptureProcessOutput(
            "nvidia-smi",
            ["--query-gpu=name", "--format=csv,noheader"],
            TimeSpan.FromSeconds(2),
            out var nvidiaOutput))
        {
            var names = SplitNonEmptyLines(nvidiaOutput);
            gpuNames.AddRange(names);
            notes.Add("nvidia-smi-detected");
        }

        if (gpuNames.Count == 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (TryCaptureProcessOutput(
                "powershell",
                [
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name"
                ],
                TimeSpan.FromSeconds(3),
                out var winGpuOutput))
            {
                gpuNames.AddRange(SplitNonEmptyLines(winGpuOutput)
                    .Where(name => !name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase)));
                notes.Add("win32-video-controller");
            }
        }

        if (gpuNames.Count == 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (TryCaptureProcessOutput("lspci", [], TimeSpan.FromSeconds(2), out var lspciOutput))
            {
                var names = SplitNonEmptyLines(lspciOutput)
                    .Where(line =>
                        line.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                gpuNames.AddRange(names);
                notes.Add("lspci-scan");
            }
        }

        if (gpuNames.Count == 0 && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (TryCaptureProcessOutput("system_profiler", ["SPDisplaysDataType"], TimeSpan.FromSeconds(3), out var macOutput))
            {
                var names = SplitNonEmptyLines(macOutput)
                    .Where(line => line.Contains("Chipset Model:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Split(':', 2)[1].Trim())
                    .ToList();
                gpuNames.AddRange(names);
                notes.Add("system-profiler-displays");
            }
        }

        var uniqueGpuNames = gpuNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasSupportedGpu = uniqueGpuNames.Any(IsSupportedGpuName);
        var preferredAcceleration = DetermineAcceleration(uniqueGpuNames);

        return new HardwareProfile(hasSupportedGpu, preferredAcceleration, uniqueGpuNames, notes.ToArray());
    }

    private static string DetermineAcceleration(IEnumerable<string> gpuNames)
    {
        if (gpuNames.Any(name => name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)))
        {
            return "cuda";
        }

        if (gpuNames.Any(name =>
                name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("radeon", StringComparison.OrdinalIgnoreCase)))
        {
            return "vulkan";
        }

        if (gpuNames.Any(name =>
                name.Contains("apple", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("m1", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("m2", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("m3", StringComparison.OrdinalIgnoreCase)))
        {
            return "metal";
        }

        return gpuNames.Any() ? "auto" : "cpu";
    }

    private static bool IsSupportedGpuName(string name)
    {
        return name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("rtx", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("gtx", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("arc", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("apple", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("m1", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("m2", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("m3", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitNonEmptyLines(string output)
    {
        return (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static bool TryCaptureProcessOutput(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        out string standardOutput)
    {
        standardOutput = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = BuildProcessStartInfo(fileName, arguments)
            };

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only.
                }

                return false;
            }

            standardOutput = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        using var process = new Process
        {
            StartInfo = BuildProcessStartInfo(fileName, arguments)
        };

        try
        {
            if (!process.Start())
            {
                return ProcessExecutionResult.Failed(exitCode: -1, standardOutput: string.Empty, standardError: "failed-to-start");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(linkedCts.Token);
            var standardOutput = await outputTask;
            var standardError = await errorTask;

            return process.ExitCode == 0
                ? ProcessExecutionResult.Succeeded(standardOutput, standardError)
                : ProcessExecutionResult.Failed(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only.
            }

            return ProcessExecutionResult.Timeout();
        }
        catch (Exception exception)
        {
            return ProcessExecutionResult.Failed(exitCode: -1, standardOutput: string.Empty, standardError: exception.Message);
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

    private sealed record ProcessExecutionResult(
        bool Success,
        int ExitCode,
        bool TimedOut,
        string StandardOutput,
        string StandardError)
    {
        public static ProcessExecutionResult Succeeded(string standardOutput, string standardError)
        {
            return new ProcessExecutionResult(true, 0, false, standardOutput, standardError);
        }

        public static ProcessExecutionResult Failed(int exitCode, string standardOutput, string standardError)
        {
            return new ProcessExecutionResult(false, exitCode, false, standardOutput, standardError);
        }

        public static ProcessExecutionResult Timeout()
        {
            return new ProcessExecutionResult(false, -1, true, string.Empty, "timeout");
        }
    }

    private sealed record HardwareProfile(
        bool HasSupportedGpu,
        string PreferredAcceleration,
        string[] GpuNames,
        string[] Notes);

    private sealed record GeneratedText(string? Text, string Source);
}

public sealed record ContinueDraftRequest(string Draft);
public sealed record ContinueDraftResponse(string Continuation, string Source);
public sealed record AssistStreamRequest(string Mode, string Draft, string? Prompt);
public sealed record AssistStreamChunk(string Text, string Source);
public sealed record SlashCommandRequest(string Command, string Draft, string? Prompt);
public sealed record SlashCommandResponse(string Output, string Source);

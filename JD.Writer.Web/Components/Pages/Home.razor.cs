using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace JD.Writer.Web.Components.Pages;

public partial class Home : ComponentBase, IAsyncDisposable
{
    private const int MaxLayersPerNote = 240;
    private const int MaxLayerSnapshotLength = 8000;
    private const string DefaultPreviewTheme = "studio";
    private static readonly TimeSpan ManualLayerCoalesceWindow = TimeSpan.FromSeconds(5);
    private static readonly List<PreviewThemeOption> PreviewThemes =
    [
        new("studio", "Studio"),
        new("paper", "Paper"),
        new("solarized", "Solarized"),
        new("terminal", "Terminal"),
        new("noir", "Noir"),
        new("blueprint", "Blueprint")
    ];
    private static readonly HashSet<string> PreviewThemeIds = PreviewThemes
        .Select(theme => theme.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private AiAssistantClient AiAssistant { get; set; } = default!;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build();

    private readonly List<NoteDocument> _notes = [];
    private readonly List<string> _hints = [];
    private readonly List<string> _help = [];
    private readonly List<string> _brainstorm = [];

    private readonly List<SlashCommandDefinition> _builtInSlashCommands = SlashCommandDefinition.CreateBuiltIns();
    private readonly List<SlashCommandDefinition> _pluginSlashCommands = [];
    private readonly List<PluginPanelDefinition> _pluginPanels = [];
    private readonly Dictionary<string, List<string>> _pluginPanelItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SlashCommandDefinition> _slashSuggestions = [];
    private readonly SemaphoreSlim _voicePipelineLock = new(1, 1);

    private string _activeNoteId = string.Empty;
    private string _searchText = string.Empty;
    private string _autocompleteSuggestion = string.Empty;
    private string _activeWord = string.Empty;
    private string _statusMessage = "Local-first mode";
    private string _lastAiSource = "fallback";
    private string _previewTheme = DefaultPreviewTheme;
    private bool _isBusy;

    private bool _isPaletteOpen;
    private string _paletteQuery = string.Empty;
    private int _paletteIndex;
    private ElementReference _paletteInput;
    private ElementReference _editorInput;

    private bool _isVoiceRecording;
    private bool _isVoiceSupported = true;
    private DotNetObjectReference<Home>? _dotNetRef;

    private CancellationTokenSource? _streamCts;
    private CancellationTokenSource? _debounceCts;

    private IEnumerable<NoteDocument> FilteredNotes =>
        _notes.Where(note =>
            string.IsNullOrWhiteSpace(_searchText) ||
            note.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            note.Content.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

    private NoteDocument? ActiveNote => _notes.FirstOrDefault(note => note.Id == _activeNoteId);
    private string ActiveWord => _activeWord;
    private int ActiveLayerCount => ActiveNote?.Layers.Count ?? 0;

    private List<PaletteCommand> PaletteResults => GetPaletteResults();
    private IReadOnlyList<PreviewThemeOption> AvailablePreviewThemes => PreviewThemes;

    private List<PanelView> AllPanels =>
    [
        new PanelView("Hints", "Live structure and clarity nudges.", _hints),
        new PanelView("Help", "Editing and markdown command assist.", _help),
        new PanelView("Brainstorm", "Prompted ideas while you write.", _brainstorm),
        new PanelView("History QC", "Version, diff, and tone checkpoints.", BuildHistoryPanelItems()),
        .. _pluginPanels.Select(panel => new PanelView(panel.Title, panel.Description, GetPluginPanelItems(panel.Id)))
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await LoadOrInitializeAsync();
        await LoadPluginManifestAsync();
        await DetectVoiceSupportAsync();
        await RefreshAssistantPanelsAsync();
    }

    private async Task DetectVoiceSupportAsync()
    {
        try
        {
            _isVoiceSupported = await JS.InvokeAsync<bool>("JDWriterStudio.isDictationSupported");
        }
        catch
        {
            _isVoiceSupported = false;
        }
    }

    private async Task LoadOrInitializeAsync()
    {
        string? serializedState = null;

        try
        {
            serializedState = await JS.InvokeAsync<string?>("JDWriterStudio.loadState");
        }
        catch (InvalidOperationException)
        {
            // JS runtime not ready yet.
        }

        if (!string.IsNullOrWhiteSpace(serializedState))
        {
            try
            {
                var state = JsonSerializer.Deserialize<StudioState>(serializedState, JsonOptions);
                if (state?.Notes is { Count: > 0 })
                {
                    _notes.Clear();
                    _notes.AddRange(state.Notes);
                    EnsureLayerHistory();
                    _activeNoteId = state.ActiveNoteId ?? _notes[0].Id;
                    if (_notes.All(note => note.Id != _activeNoteId))
                    {
                        _activeNoteId = _notes[0].Id;
                    }
                    _previewTheme = NormalizePreviewTheme(state.PreviewTheme);
                    UpdateEditorSignals();
                    StateHasChanged();
                    return;
                }
            }
            catch (JsonException)
            {
                // Ignore corrupted local state and seed starter notes.
            }
        }

        _notes.Clear();
        _notes.AddRange(NoteDocument.CreateStarterSet());
        EnsureLayerHistory();
        _activeNoteId = _notes[0].Id;
        UpdateEditorSignals();
        await PersistStateAsync();
        StateHasChanged();
    }

    private async Task LoadPluginManifestAsync()
    {
        string? manifestText = null;

        try
        {
            manifestText = await JS.InvokeAsync<string?>("JDWriterStudio.loadPluginManifest");
        }
        catch (InvalidOperationException)
        {
            // JS runtime not ready yet.
        }

        _pluginSlashCommands.Clear();
        _pluginPanels.Clear();
        _pluginPanelItems.Clear();

        if (string.IsNullOrWhiteSpace(manifestText))
        {
            return;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestText, JsonOptions);
            if (manifest is null)
            {
                return;
            }

            foreach (var slash in manifest.SlashCommands)
            {
                if (string.IsNullOrWhiteSpace(slash.Name))
                {
                    continue;
                }

                _pluginSlashCommands.Add(new SlashCommandDefinition
                {
                    Name = slash.Name.Trim().ToLowerInvariant(),
                    Description = string.IsNullOrWhiteSpace(slash.Description) ? "Plugin slash command" : slash.Description,
                    Kind = string.IsNullOrWhiteSpace(slash.Kind) ? "template" : slash.Kind,
                    Template = slash.Template,
                    Prompt = slash.Prompt
                });
            }

            foreach (var panel in manifest.Panels)
            {
                if (string.IsNullOrWhiteSpace(panel.Id) || string.IsNullOrWhiteSpace(panel.Mode))
                {
                    continue;
                }

                _pluginPanels.Add(new PluginPanelDefinition
                {
                    Id = panel.Id,
                    Title = string.IsNullOrWhiteSpace(panel.Title) ? panel.Id : panel.Title,
                    Description = string.IsNullOrWhiteSpace(panel.Description) ? "Plugin panel" : panel.Description,
                    Mode = panel.Mode,
                    Prompt = panel.Prompt
                });

                _pluginPanelItems[panel.Id] = [];
            }

            _statusMessage = "Plugin manifest loaded";
        }
        catch (JsonException)
        {
            _statusMessage = "Plugin manifest ignored (invalid JSON)";
        }

        UpdateSlashSuggestions();
        StateHasChanged();
    }

    private void SelectNote(string noteId)
    {
        _activeNoteId = noteId;
        UpdateEditorSignals();
        QueueAssistantRefresh();
    }

    private async Task CreateNoteAsync()
    {
        var note = new NoteDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Untitled note",
            Content = "# New note\n\n",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        EnsureInitialLayer(note, "note-created", "local", "New note seeded");
        _notes.Insert(0, note);
        _activeNoteId = note.Id;
        UpdateEditorSignals();
        await PersistStateAsync();
        await RefreshAssistantPanelsAsync();
    }

    private async Task HandleTitleInput(ChangeEventArgs args)
    {
        if (ActiveNote is null)
        {
            return;
        }

        var titleBefore = ActiveNote.Title;
        var contentBefore = ActiveNote.Content;
        ActiveNote.Title = string.IsNullOrWhiteSpace(args.Value?.ToString()) ? "Untitled note" : args.Value!.ToString()!;
        ActiveNote.UpdatedAt = DateTimeOffset.UtcNow;

        RecordLayer(
            ActiveNote,
            operation: "title-edit",
            source: "local",
            contentBefore: contentBefore,
            contentAfter: ActiveNote.Content,
            titleBefore: titleBefore,
            titleAfter: ActiveNote.Title,
            annotation: "Title updated");

        await PersistStateAsync();
        QueueAssistantRefresh();
    }

    private async Task HandleDraftInput(ChangeEventArgs args)
    {
        if (ActiveNote is null)
        {
            return;
        }

        var before = ActiveNote.Content;
        ActiveNote.Content = args.Value?.ToString() ?? string.Empty;
        ActiveNote.UpdatedAt = DateTimeOffset.UtcNow;

        RecordLayer(
            ActiveNote,
            operation: "manual-edit",
            source: "local",
            contentBefore: before,
            contentAfter: ActiveNote.Content,
            titleBefore: ActiveNote.Title,
            titleAfter: ActiveNote.Title,
            annotation: "Editor input");

        UpdateEditorSignals();
        await PersistStateAsync();
        QueueAssistantRefresh();
    }

    private async Task HandleEditorKeyDown(KeyboardEventArgs args)
    {
        if (args.CtrlKey && string.Equals(args.Key, "k", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPaletteAsync();
            return;
        }

        if (args.CtrlKey && string.Equals(args.Key, "j", StringComparison.OrdinalIgnoreCase))
        {
            await ContinueWithAiAsync();
            return;
        }

        if (args.CtrlKey &&
            (string.Equals(args.Key, "m", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args.Code, "KeyM", StringComparison.OrdinalIgnoreCase)))
        {
            await ToggleVoiceCaptureAsync();
            return;
        }

        if (args.CtrlKey && string.Equals(args.Key, "Enter", StringComparison.OrdinalIgnoreCase) && _slashSuggestions.Count > 0)
        {
            await ApplySlashCommandAsync(_slashSuggestions[0]);
            return;
        }

        if (args.CtrlKey && string.Equals(args.Code, "Space", StringComparison.OrdinalIgnoreCase))
        {
            await AcceptAutocompleteAsync();
        }
    }

    private async Task HandlePaletteKeyDown(KeyboardEventArgs args)
    {
        var results = PaletteResults;

        if (string.Equals(args.Key, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            ClosePalette();
            return;
        }

        if (string.Equals(args.Key, "ArrowDown", StringComparison.OrdinalIgnoreCase))
        {
            _paletteIndex = results.Count == 0 ? 0 : Math.Min(_paletteIndex + 1, results.Count - 1);
            return;
        }

        if (string.Equals(args.Key, "ArrowUp", StringComparison.OrdinalIgnoreCase))
        {
            _paletteIndex = results.Count == 0 ? 0 : Math.Max(_paletteIndex - 1, 0);
            return;
        }

        if (string.Equals(args.Key, "Enter", StringComparison.OrdinalIgnoreCase) && results.Count > 0)
        {
            await ExecutePaletteCommandAsync(results[_paletteIndex]);
        }
    }

    private async Task OpenPaletteAsync()
    {
        _isPaletteOpen = true;
        _paletteQuery = string.Empty;
        _paletteIndex = 0;
        await InvokeAsync(StateHasChanged);
        await Task.Delay(1);
        await _paletteInput.FocusAsync();
    }

    private void ClosePalette() => _isPaletteOpen = false;

    private async Task ExecutePaletteCommandAsync(PaletteCommand command)
    {
        switch (command.Action)
        {
            case PaletteAction.NewNote:
                await CreateNoteAsync();
                break;
            case PaletteAction.ContinueDraft:
                await ContinueWithAiAsync();
                break;
            case PaletteAction.ToggleVoiceCapture:
                await ToggleVoiceCaptureAsync();
                break;
            case PaletteAction.RefreshInsights:
                await RefreshAssistantPanelsAsync();
                break;
            case PaletteAction.ExportNote:
                await DownloadActiveNoteAsync();
                break;
            case PaletteAction.SlashCommand:
                var slash = GetSlashCommandByName(command.Arg ?? string.Empty);
                if (slash is not null)
                {
                    await ApplySlashCommandAsync(slash);
                }
                break;
        }

        ClosePalette();
    }

    private List<PaletteCommand> GetPaletteResults()
    {
        var commands = BuildPaletteCommands();

        if (!string.IsNullOrWhiteSpace(_paletteQuery))
        {
            commands = commands.Where(command =>
                    command.Title.Contains(_paletteQuery, StringComparison.OrdinalIgnoreCase) ||
                    command.Description.Contains(_paletteQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (_paletteIndex >= commands.Count)
        {
            _paletteIndex = Math.Max(0, commands.Count - 1);
        }

        return commands.Take(12).ToList();
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var commands = new List<PaletteCommand>
        {
            new() { Title = "Create note", Description = "Create a new markdown note.", Action = PaletteAction.NewNote },
            new() { Title = "AI continue", Description = "Continue the current draft with AI.", Action = PaletteAction.ContinueDraft },
            new() { Title = _isVoiceRecording ? "Stop voice capture" : "Start voice capture", Description = "Toggle voice dictation at the cursor.", Action = PaletteAction.ToggleVoiceCapture },
            new() { Title = "Refresh insights", Description = "Refresh hints/help/brainstorm streams.", Action = PaletteAction.RefreshInsights },
            new() { Title = "Export markdown", Description = "Download active note as .md.", Action = PaletteAction.ExportNote }
        };

        commands.AddRange(AllSlashCommands().Select(command => new PaletteCommand
        {
            Title = $"Run /{command.Name}",
            Description = command.Description,
            Action = PaletteAction.SlashCommand,
            Arg = command.Name
        }));

        return commands;
    }

    private SlashCommandDefinition? GetSlashCommandByName(string name)
    {
        return AllSlashCommands().FirstOrDefault(command =>
            string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ContinueWithAiAsync()
    {
        if (ActiveNote is null)
        {
            return;
        }

        _isBusy = true;
        _statusMessage = "Generating continuation...";
        var before = ActiveNote.Content;

        try
        {
            var response = await AiAssistant.ContinueDraftAsync(new ContinueDraftRequest(TrimForPrompt(ActiveNote.Content)));
            if (!string.IsNullOrWhiteSpace(response.Continuation))
            {
                ActiveNote.Content = AppendToDraft(ActiveNote.Content, response.Continuation.Trim());
                ActiveNote.UpdatedAt = DateTimeOffset.UtcNow;
                _lastAiSource = response.Source;
                _statusMessage = "Continuation inserted";

                RecordLayer(
                    ActiveNote,
                    operation: "ai-continue",
                    source: response.Source,
                    contentBefore: before,
                    contentAfter: ActiveNote.Content,
                    titleBefore: ActiveNote.Title,
                    titleAfter: ActiveNote.Title,
                    annotation: "Toolbar AI continue");

                UpdateEditorSignals();
                await PersistStateAsync();
                QueueAssistantRefresh();
            }
        }
        catch (Exception)
        {
            _statusMessage = "AI continuation unavailable";
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ToggleVoiceCaptureAsync()
    {
        if (_isVoiceRecording)
        {
            await StopVoiceCaptureAsync();
            return;
        }

        await StartVoiceCaptureAsync();
    }

    private async Task StartVoiceCaptureAsync()
    {
        _dotNetRef ??= DotNetObjectReference.Create(this);

        DictationStartResult? result;
        try
        {
            result = await JS.InvokeAsync<DictationStartResult>("JDWriterStudio.startDictation", _dotNetRef);
        }
        catch
        {
            _isVoiceSupported = false;
            _statusMessage = "Voice capture unavailable in this browser";
            return;
        }

        if (result?.Started == true)
        {
            _isVoiceRecording = true;
            _statusMessage = "Voice capture active";
            StateHasChanged();
            return;
        }

        if (string.Equals(result?.Reason, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            _isVoiceSupported = false;
            _statusMessage = "Voice capture unavailable in this browser";
        }
        else
        {
            _statusMessage = "Voice capture could not start";
        }
    }

    private async Task StopVoiceCaptureAsync(string statusMessage = "Voice capture stopped")
    {
        try
        {
            await JS.InvokeVoidAsync("JDWriterStudio.stopDictation");
        }
        catch
        {
            // Ignore stop failures.
        }

        _isVoiceRecording = false;
        _statusMessage = statusMessage;
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnVoiceCaptureStatusChanged(string status)
    {
        return InvokeAsync(() =>
        {
            if (string.Equals(status, "recording", StringComparison.OrdinalIgnoreCase))
            {
                _isVoiceRecording = true;
                _statusMessage = "Voice capture active";
            }
            else if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                _isVoiceRecording = false;
                if (!string.Equals(_statusMessage, "Voice transcript cleaned", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_statusMessage, "Voice transcript inserted", StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = "Voice capture stopped";
                }
            }
            else if (status.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                _isVoiceRecording = false;
                _statusMessage = "Voice capture error";
            }

            StateHasChanged();
        });
    }

    [JSInvokable]
    public async Task OnVoiceTranscriptFinalized(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        await InvokeAsync(async () =>
        {
            if (ActiveNote is null)
            {
                return;
            }

            await _voicePipelineLock.WaitAsync();
            try
            {
                await InsertTranscriptAtCursorAsync(ActiveNote, transcript);
            }
            finally
            {
                _voicePipelineLock.Release();
            }
        });
    }

    private async Task InsertTranscriptAtCursorAsync(NoteDocument note, string transcript)
    {
        var normalizedTranscript = transcript.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return;
        }

        var before = note.Content;
        EditorInsertionResult? insertion = null;

        try
        {
            insertion = await JS.InvokeAsync<EditorInsertionResult>(
                "JDWriterStudio.insertTextAtCursor",
                _editorInput,
                normalizedTranscript);
        }
        catch
        {
            // Fallback to append if direct cursor insertion fails.
        }

        if (insertion is not null && !string.IsNullOrWhiteSpace(insertion.Value))
        {
            note.Content = insertion.Value;
        }
        else
        {
            note.Content = AppendToDraft(note.Content, normalizedTranscript);
        }

        note.UpdatedAt = DateTimeOffset.UtcNow;
        _statusMessage = "Voice transcript inserted";

        RecordLayer(
            note,
            operation: "voice-transcript",
            source: "browser-speech",
            contentBefore: before,
            contentAfter: note.Content,
            titleBefore: note.Title,
            titleAfter: note.Title,
            annotation: normalizedTranscript);

        UpdateEditorSignals();
        await PersistStateAsync();
        QueueAssistantRefresh();
        StateHasChanged();

        if (insertion is null)
        {
            return;
        }

        var insertedSegment = SafeSlice(note.Content, insertion.Start, insertion.End);
        if (string.IsNullOrWhiteSpace(insertedSegment))
        {
            return;
        }

        await ApplyAiVoiceCleanupAsync(note, insertion.Start, insertion.End, insertedSegment, normalizedTranscript);
    }

    private async Task ApplyAiVoiceCleanupAsync(NoteDocument note, int rangeStart, int rangeEnd, string insertedSegment, string rawTranscript)
    {
        try
        {
            var response = await AiAssistant.RunSlashCommandAsync(new SlashCommandRequest(
                "voice-cleanup",
                TrimForPrompt(rawTranscript),
                "Clean this voice transcript into concise markdown. Preserve intent and factual meaning. Return markdown only."));

            var cleaned = response.Output?.Trim();
            var cleanupNoOp = string.IsNullOrWhiteSpace(cleaned) || string.Equals(cleaned, insertedSegment, StringComparison.Ordinal);
            if (cleanupNoOp)
            {
                RecordLayer(
                    note,
                    operation: "voice-cleanup-attempt",
                    source: response.Source,
                    contentBefore: note.Content,
                    contentAfter: note.Content,
                    titleBefore: note.Title,
                    titleAfter: note.Title,
                    annotation: "Cleanup attempt returned no material change");
                await PersistStateAsync();
                return;
            }

            if (ActiveNote is null || ActiveNote.Id != note.Id)
            {
                return;
            }

            if (!RangeMatches(note.Content, rangeStart, rangeEnd, insertedSegment))
            {
                _statusMessage = "Voice transcript captured; cleanup skipped due to concurrent edits";
                return;
            }

            var before = note.Content;
            note.Content = ReplaceRange(note.Content, rangeStart, rangeEnd, cleaned!);
            note.UpdatedAt = DateTimeOffset.UtcNow;
            _statusMessage = "Voice transcript cleaned";
            _lastAiSource = response.Source;

            RecordLayer(
                note,
                operation: "voice-cleanup",
                source: response.Source,
                contentBefore: before,
                contentAfter: note.Content,
                titleBefore: note.Title,
                titleAfter: note.Title,
                annotation: "AI normalized transcript at insertion range");

            UpdateEditorSignals();
            await PersistStateAsync();
            QueueAssistantRefresh();
            StateHasChanged();
        }
        catch
        {
            RecordLayer(
                note,
                operation: "voice-cleanup-attempt",
                source: "fallback",
                contentBefore: note.Content,
                contentAfter: note.Content,
                titleBefore: note.Title,
                titleAfter: note.Title,
                annotation: "Cleanup attempt failed");
            await PersistStateAsync();
            _statusMessage = "Voice transcript captured; AI cleanup unavailable";
        }
    }

    private async Task AcceptAutocompleteAsync()
    {
        if (ActiveNote is null || string.IsNullOrWhiteSpace(_autocompleteSuggestion))
        {
            return;
        }

        var before = ActiveNote.Content;
        ActiveNote.Content += _autocompleteSuggestion;
        ActiveNote.UpdatedAt = DateTimeOffset.UtcNow;

        RecordLayer(
            ActiveNote,
            operation: "autocomplete",
            source: "local-autocomplete",
            contentBefore: before,
            contentAfter: ActiveNote.Content,
            titleBefore: ActiveNote.Title,
            titleAfter: ActiveNote.Title,
            annotation: "Autocomplete accepted");

        UpdateEditorSignals();
        await PersistStateAsync();
        QueueAssistantRefresh();
    }

    private async Task ApplySlashCommandAsync(SlashCommandDefinition command)
    {
        if (ActiveNote is null)
        {
            return;
        }

        var before = ActiveNote.Content;
        RemoveTrailingSlashContext(ActiveNote);

        string output;
        string source;
        if (string.Equals(command.Kind, "template", StringComparison.OrdinalIgnoreCase))
        {
            output = command.Template ?? string.Empty;
            source = "plugin-template";
        }
        else
        {
            var response = await AiAssistant.RunSlashCommandAsync(new SlashCommandRequest(command.Name, TrimForPrompt(ActiveNote.Content), command.Prompt));
            output = response.Output;
            _lastAiSource = response.Source;
            source = response.Source;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        ActiveNote.Content = AppendToDraft(ActiveNote.Content, output.Trim());
        ActiveNote.UpdatedAt = DateTimeOffset.UtcNow;
        _statusMessage = $"Slash /{command.Name} applied";

        RecordLayer(
            ActiveNote,
            operation: "slash-command",
            source: source,
            contentBefore: before,
            contentAfter: ActiveNote.Content,
            titleBefore: ActiveNote.Title,
            titleAfter: ActiveNote.Title,
            annotation: $"/{command.Name}");

        UpdateEditorSignals();
        await PersistStateAsync();
        QueueAssistantRefresh();
    }

    private async Task DownloadActiveNoteAsync()
    {
        if (ActiveNote is null)
        {
            return;
        }

        var fileName = SlugifyTitle(ActiveNote.Title) + ".md";
        await JS.InvokeVoidAsync("JDWriterStudio.downloadMarkdown", fileName, ActiveNote.Content);
    }

    private void QueueAssistantRefresh()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(_debounceCts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(950), cancellationToken);
            await RefreshAssistantPanelsAsync();
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled debounce.
        }
    }

    private async Task RefreshAssistantPanelsAsync()
    {
        if (ActiveNote is null)
        {
            return;
        }

        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();

        _hints.Clear();
        _help.Clear();
        _brainstorm.Clear();

        foreach (var panel in _pluginPanels)
        {
            GetPluginPanelItems(panel.Id).Clear();
        }

        var token = _streamCts.Token;
        var tasks = new List<Task>
        {
            ConsumeStreamAsync("hints", _hints, token, null),
            ConsumeStreamAsync("help", _help, token, null),
            ConsumeStreamAsync("brainstorm", _brainstorm, token, null)
        };

        foreach (var panel in _pluginPanels)
        {
            tasks.Add(ConsumeStreamAsync(panel.Mode, GetPluginPanelItems(panel.Id), token, panel.Prompt));
        }

        try
        {
            await Task.WhenAll(tasks);
            _statusMessage = "Insights updated";
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled refresh.
        }
    }

    private async Task ConsumeStreamAsync(string mode, List<string> target, CancellationToken cancellationToken, string? prompt)
    {
        try
        {
            var receivedAny = false;
            await foreach (var chunk in AiAssistant.StreamAssistAsync(mode, TrimForPrompt(ActiveNote?.Content ?? string.Empty), prompt, cancellationToken))
            {
                receivedAny = true;
                target.Add(chunk.Text);
                _lastAiSource = chunk.Source;
                await InvokeAsync(StateHasChanged);
            }

            if (!receivedAny && !cancellationToken.IsCancellationRequested)
            {
                target.Add("No insight returned. Refresh to retry.");
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            target.Add("AI stream unavailable. Local heuristics still active.");
            _lastAiSource = "fallback";
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PersistStateAsync()
    {
        var state = new StudioState
        {
            ActiveNoteId = _activeNoteId,
            PreviewTheme = _previewTheme,
            Notes = [.. _notes]
        };

        var serialized = JsonSerializer.Serialize(state, JsonOptions);
        await JS.InvokeVoidAsync("JDWriterStudio.saveState", serialized);
    }

    private async Task HandlePreviewThemeChangedAsync(ChangeEventArgs args)
    {
        var nextTheme = NormalizePreviewTheme(args.Value?.ToString());
        if (string.Equals(nextTheme, _previewTheme, StringComparison.Ordinal))
        {
            return;
        }

        _previewTheme = nextTheme;
        _statusMessage = $"Preview theme: {ResolvePreviewThemeLabel(nextTheme)}";
        await PersistStateAsync();
    }

    private void UpdateEditorSignals()
    {
        UpdateAutocomplete();
        UpdateSlashSuggestions();
    }

    private void UpdateAutocomplete()
    {
        _autocompleteSuggestion = string.Empty;
        _activeWord = string.Empty;

        if (ActiveNote is null || string.IsNullOrWhiteSpace(ActiveNote.Content))
        {
            return;
        }

        var activeMatch = Regex.Match(ActiveNote.Content, @"([A-Za-z][A-Za-z\-]{1,})$");
        if (!activeMatch.Success)
        {
            return;
        }

        var prefix = activeMatch.Groups[1].Value;
        _activeWord = prefix;

        var corpus = string.Join(' ', _notes.Select(note => note.Content));
        var candidate = Regex.Matches(corpus, @"\b[A-Za-z][A-Za-z\-]{2,}\b")
            .Select(match => match.Value)
            .Where(token => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(token, prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(token => token, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Length)
            .Select(group => group.Key)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length > prefix.Length)
        {
            _autocompleteSuggestion = candidate[prefix.Length..];
        }
    }

    private void UpdateSlashSuggestions()
    {
        _slashSuggestions.Clear();

        if (!TryGetSlashContext(out var token, out _, out _))
        {
            return;
        }

        var matches = AllSlashCommands()
            .Where(command => command.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name)
            .Take(8)
            .ToList();

        _slashSuggestions.AddRange(matches);
    }

    private bool TryGetSlashContext(out string token, out int startIndex, out int length)
    {
        token = string.Empty;
        startIndex = 0;
        length = 0;

        if (ActiveNote is null || string.IsNullOrWhiteSpace(ActiveNote.Content))
        {
            return false;
        }

        var content = ActiveNote.Content;
        var lineStart = content.LastIndexOf('\n');
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var tail = content[lineStart..].TrimEnd('\r');
        var match = Regex.Match(tail, @"^/([a-z0-9\-]*)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        token = match.Groups[1].Value;
        startIndex = lineStart;
        length = tail.Length;
        return true;
    }

    private void RemoveTrailingSlashContext(NoteDocument note)
    {
        if (!TryGetSlashContext(out _, out var start, out var length))
        {
            return;
        }

        note.Content = note.Content.Remove(start, length).TrimEnd();
    }

    private IEnumerable<SlashCommandDefinition> AllSlashCommands() => _builtInSlashCommands.Concat(_pluginSlashCommands);

    private List<string> GetPluginPanelItems(string panelId)
    {
        if (!_pluginPanelItems.TryGetValue(panelId, out var values))
        {
            values = [];
            _pluginPanelItems[panelId] = values;
        }

        return values;
    }

    private List<string> BuildHistoryPanelItems()
    {
        if (ActiveNote is null)
        {
            return ["No active note selected."];
        }

        if (ActiveNote.Layers.Count == 0)
        {
            return ["No history layers captured yet."];
        }

        var recent = ActiveNote.Layers
            .OrderByDescending(layer => layer.UpdatedAt)
            .Take(8)
            .Select(layer =>
            {
                var netChars = layer.Diff?.NetCharacters ?? 0;
                var netText = netChars > 0 ? $"+{netChars}" : netChars.ToString();
                var tone = layer.Tone?.Label ?? "neutral";
                var sentiment = layer.Tone?.Sentiment ?? 0;
                return $"{layer.UpdatedAt.ToLocalTime():MMM d h:mm tt} | {layer.Operation} | delta {netText}c | tone {tone} ({sentiment:+0.00;-0.00;0.00})";
            })
            .ToList();

        if (ActiveNote.Layers.Count >= 2)
        {
            var newest = ActiveNote.Layers[^1].Tone?.Sentiment ?? 0;
            var previous = ActiveNote.Layers[^2].Tone?.Sentiment ?? 0;
            var drift = Math.Abs(newest - previous);
            recent.Insert(0, $"Tone drift vs previous layer: {drift:0.00}");
        }

        return recent;
    }

    private void EnsureLayerHistory()
    {
        foreach (var note in _notes)
        {
            if (note.Layers.Count == 0)
            {
                EnsureInitialLayer(note, "seed", "local", "Initial capture");
            }
            else
            {
                if (note.Layers.Count > MaxLayersPerNote)
                {
                    note.Layers.RemoveRange(0, note.Layers.Count - MaxLayersPerNote);
                }

                foreach (var layer in note.Layers)
                {
                    layer.Diff ??= BuildDiffMetrics(string.Empty, note.Content);
                    layer.Tone ??= AnalyzeTone(note.Content);
                    layer.UpdatedAt = layer.UpdatedAt == default ? layer.CreatedAt : layer.UpdatedAt;
                }
            }
        }
    }

    private void EnsureInitialLayer(NoteDocument note, string operation, string source, string annotation)
    {
        if (note.Layers.Count > 0)
        {
            return;
        }

        var snapshot = note.Content ?? string.Empty;
        note.Layers.Add(new NoteLayer
        {
            LayerId = Guid.NewGuid().ToString("N"),
            CreatedAt = note.UpdatedAt,
            UpdatedAt = note.UpdatedAt,
            Operation = operation,
            Source = source,
            TitleBefore = note.Title,
            TitleAfter = note.Title,
            Diff = BuildDiffMetrics(string.Empty, snapshot),
            Tone = AnalyzeTone(snapshot),
            Snapshot = CaptureSnapshot(snapshot),
            Annotation = annotation
        });
    }

    private void RecordLayer(
        NoteDocument note,
        string operation,
        string source,
        string contentBefore,
        string contentAfter,
        string titleBefore,
        string titleAfter,
        string? annotation)
    {
        var now = DateTimeOffset.UtcNow;
        var diff = BuildDiffMetrics(contentBefore, contentAfter);
        var tone = AnalyzeTone(contentAfter);

        if (string.Equals(operation, "manual-edit", StringComparison.OrdinalIgnoreCase) &&
            note.Layers.Count > 0)
        {
            var last = note.Layers[^1];
            if (string.Equals(last.Operation, "manual-edit", StringComparison.OrdinalIgnoreCase) &&
                now - last.UpdatedAt <= ManualLayerCoalesceWindow)
            {
                last.UpdatedAt = now;
                last.Source = source;
                last.TitleAfter = titleAfter;
                last.Diff = diff;
                last.Tone = tone;
                last.Snapshot = CaptureSnapshot(contentAfter);
                last.Annotation = annotation;
                return;
            }
        }

        note.Layers.Add(new NoteLayer
        {
            LayerId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            Operation = operation,
            Source = source,
            TitleBefore = titleBefore,
            TitleAfter = titleAfter,
            Diff = diff,
            Tone = tone,
            Snapshot = CaptureSnapshot(contentAfter),
            Annotation = annotation
        });

        if (note.Layers.Count > MaxLayersPerNote)
        {
            note.Layers.RemoveRange(0, note.Layers.Count - MaxLayersPerNote);
        }
    }

    private static DiffMetrics BuildDiffMetrics(string before, string after)
    {
        before ??= string.Empty;
        after ??= string.Empty;

        var minLength = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < minLength && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < minLength - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        var removed = Math.Max(0, before.Length - prefix - suffix);
        var added = Math.Max(0, after.Length - prefix - suffix);

        var beforeLines = CountLines(before);
        var afterLines = CountLines(after);

        return new DiffMetrics
        {
            AddedCharacters = added,
            RemovedCharacters = removed,
            NetCharacters = after.Length - before.Length,
            BeforeLines = beforeLines,
            AfterLines = afterLines,
            ChangedLineEstimate = Math.Abs(afterLines - beforeLines) + (added + removed > 0 ? 1 : 0)
        };
    }

    private static ToneMetrics AnalyzeTone(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ToneMetrics { Label = "neutral", Sentiment = 0, Urgency = 0, Confidence = 0, Formality = 0 };
        }

        var tokens = Regex.Matches(content.ToLowerInvariant(), @"[a-z']+")
            .Select(match => match.Value)
            .ToList();

        if (tokens.Count == 0)
        {
            return new ToneMetrics { Label = "neutral", Sentiment = 0, Urgency = 0, Confidence = 0, Formality = 0 };
        }

        var positiveLexicon = new HashSet<string>
        {
            "clear", "great", "solid", "confident", "good", "improve", "win", "stable", "strong", "excellent", "ready", "done"
        };

        var negativeLexicon = new HashSet<string>
        {
            "risk", "issue", "blocker", "problem", "delay", "broken", "unclear", "concern", "fail", "failed", "hard"
        };

        var urgentLexicon = new HashSet<string>
        {
            "urgent", "asap", "immediately", "now", "today", "critical", "priority"
        };

        var confidenceLexicon = new HashSet<string>
        {
            "will", "must", "definitely", "certain", "committed", "guarantee"
        };

        var uncertaintyLexicon = new HashSet<string>
        {
            "maybe", "might", "perhaps", "possibly", "unclear", "guess"
        };

        var positive = tokens.Count(token => positiveLexicon.Contains(token));
        var negative = tokens.Count(token => negativeLexicon.Contains(token));
        var urgent = tokens.Count(token => urgentLexicon.Contains(token));
        var confident = tokens.Count(token => confidenceLexicon.Contains(token));
        var uncertain = tokens.Count(token => uncertaintyLexicon.Contains(token));

        var sentiment = (positive - negative) / (double)tokens.Count;
        var urgency = urgent / (double)tokens.Count;
        var confidence = (confident - uncertain) / (double)tokens.Count;

        var longWordCount = tokens.Count(token => token.Length >= 7);
        var punctuationCount = content.Count(ch => ch == ',' || ch == ';' || ch == ':');
        var formality = (longWordCount + punctuationCount) / Math.Max(1d, tokens.Count);

        var label = "neutral";
        if (urgency > 0.06)
        {
            label = "urgent";
        }
        else if (sentiment > 0.08)
        {
            label = "optimistic";
        }
        else if (sentiment < -0.08)
        {
            label = "critical";
        }
        else if (formality > 0.28)
        {
            label = "formal";
        }

        return new ToneMetrics
        {
            Label = label,
            Sentiment = Math.Round(sentiment, 3),
            Urgency = Math.Round(urgency, 3),
            Confidence = Math.Round(confidence, 3),
            Formality = Math.Round(formality, 3)
        };
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Count(ch => ch == '\n') + 1;
    }

    private static string CaptureSnapshot(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= MaxLayerSnapshotLength)
        {
            return content;
        }

        var keep = MaxLayerSnapshotLength / 2;
        return content[..keep] + "\n...\n" + content[^keep..];
    }

    private static string SafeSlice(string content, int start, int end)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var safeStart = Math.Clamp(start, 0, content.Length);
        var safeEnd = Math.Clamp(end, safeStart, content.Length);
        return content[safeStart..safeEnd];
    }

    private static bool RangeMatches(string content, int start, int end, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return string.Equals(SafeSlice(content, start, end), expected, StringComparison.Ordinal);
    }

    private static string ReplaceRange(string content, int start, int end, string replacement)
    {
        var safeStart = Math.Clamp(start, 0, content.Length);
        var safeEnd = Math.Clamp(end, safeStart, content.Length);
        return content[..safeStart] + replacement + content[safeEnd..];
    }

    private static string FormatPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "Empty";
        }

        var collapsed = content.Replace('\n', ' ').Trim();
        return collapsed.Length <= 56 ? collapsed : collapsed[..56] + "...";
    }

    private static string SlugifyTitle(string title)
    {
        var cleaned = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\-\s]", string.Empty).Trim();
        var compact = Regex.Replace(cleaned, @"\s+", "-");
        return string.IsNullOrWhiteSpace(compact) ? "note" : compact;
    }

    private static string AppendToDraft(string draft, string addition)
    {
        if (string.IsNullOrWhiteSpace(draft))
        {
            return addition;
        }

        var divider = draft.EndsWith('\n') ? "\n" : "\n\n";
        return draft + divider + addition;
    }

    private static string TrimForPrompt(string draft)
    {
        return draft.Length <= 4500 ? draft : draft[^4500..];
    }

    private static string RenderMarkdown(string markdown)
    {
        return Markdown.ToHtml(markdown ?? string.Empty, MarkdownPipeline);
    }

    private static string NormalizePreviewTheme(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return DefaultPreviewTheme;
        }

        var normalized = themeId.Trim().ToLowerInvariant();
        return PreviewThemeIds.Contains(normalized) ? normalized : DefaultPreviewTheme;
    }

    private static string ResolvePreviewThemeLabel(string themeId)
    {
        return PreviewThemes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase))?.Label ?? "Studio";
    }

    public async ValueTask DisposeAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        if (_isVoiceRecording)
        {
            try
            {
                await JS.InvokeVoidAsync("JDWriterStudio.stopDictation");
            }
            catch
            {
                // No-op during disposal.
            }
        }

        _dotNetRef?.Dispose();
        _voicePipelineLock.Dispose();
    }

    private sealed class StudioState
    {
        public string? ActiveNoteId { get; set; }
        public string PreviewTheme { get; set; } = DefaultPreviewTheme;
        public List<NoteDocument> Notes { get; set; } = [];
    }

    private sealed class NoteDocument
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Untitled note";
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<NoteLayer> Layers { get; set; } = [];

        public static List<NoteDocument> CreateStarterSet()
        {
            return
            [
                new NoteDocument
                {
                    Title = "Launch plan",
                    Content = "# JD.Writer launch\n\n## Today\n- Build local-first markdown capture\n- Add AI continue\n- Stream hints/help/brainstorm\n\n## Questions\n- Voice notes in v2?\n",
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new NoteDocument
                {
                    Title = "Idea dump",
                    Content = "# Idea dump\n\n- Project codename: Signal Notebooks\n- Daily capture flow\n- Lightweight plugin API\n",
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-8)
                }
            ];
        }
    }

    private sealed class PluginManifest
    {
        public List<PluginSlashManifest> SlashCommands { get; set; } = [];
        public List<PluginPanelManifest> Panels { get; set; } = [];
    }

    private sealed class PluginSlashManifest
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Kind { get; set; } = "template";
        public string? Template { get; set; }
        public string? Prompt { get; set; }
    }

    private sealed class PluginPanelManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string? Prompt { get; set; }
    }

    private sealed class PluginPanelDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Mode { get; init; } = string.Empty;
        public string? Prompt { get; init; }
    }

    private sealed class SlashCommandDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Kind { get; init; } = "template";
        public string? Template { get; init; }
        public string? Prompt { get; init; }

        public static List<SlashCommandDefinition> CreateBuiltIns()
        {
            return
            [
                new SlashCommandDefinition
                {
                    Name = "summarize",
                    Description = "Summarize the current draft into concise markdown bullets.",
                    Kind = "ai",
                    Prompt = "Summarize this draft in concise markdown bullets with short headings."
                },
                new SlashCommandDefinition
                {
                    Name = "outline",
                    Description = "Convert the draft into a structured markdown outline.",
                    Kind = "ai",
                    Prompt = "Convert this draft into a structured markdown outline with clear sections."
                },
                new SlashCommandDefinition
                {
                    Name = "action-items",
                    Description = "Extract actionable tasks from the current draft.",
                    Kind = "ai",
                    Prompt = "Extract actionable tasks from this draft as markdown checkboxes."
                }
            ];
        }
    }

    private sealed class NoteLayer
    {
        public string LayerId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Operation { get; set; } = "manual-edit";
        public string Source { get; set; } = "local";
        public string TitleBefore { get; set; } = string.Empty;
        public string TitleAfter { get; set; } = string.Empty;
        public DiffMetrics? Diff { get; set; } = new();
        public ToneMetrics? Tone { get; set; } = new();
        public string Snapshot { get; set; } = string.Empty;
        public string? Annotation { get; set; }
    }

    private sealed class DiffMetrics
    {
        public int AddedCharacters { get; set; }
        public int RemovedCharacters { get; set; }
        public int NetCharacters { get; set; }
        public int BeforeLines { get; set; }
        public int AfterLines { get; set; }
        public int ChangedLineEstimate { get; set; }
    }

    private sealed class ToneMetrics
    {
        public string Label { get; set; } = "neutral";
        public double Sentiment { get; set; }
        public double Urgency { get; set; }
        public double Confidence { get; set; }
        public double Formality { get; set; }
    }

    private sealed class DictationStartResult
    {
        public bool Started { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class EditorInsertionResult
    {
        public string Value { get; set; } = string.Empty;
        public int Start { get; set; }
        public int End { get; set; }
    }

    private sealed class PaletteCommand
    {
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public PaletteAction Action { get; init; }
        public string? Arg { get; init; }
    }

    private enum PaletteAction
    {
        NewNote,
        ContinueDraft,
        ToggleVoiceCapture,
        RefreshInsights,
        ExportNote,
        SlashCommand
    }

    private sealed record PanelView(string Title, string Description, List<string> Items);
    private sealed record PreviewThemeOption(string Id, string Label);
}

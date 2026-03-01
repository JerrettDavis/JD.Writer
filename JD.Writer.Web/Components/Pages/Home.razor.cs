using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace JD.Writer.Web.Components.Pages;

public partial class Home : ComponentBase, IAsyncDisposable
{
    private const int MaxLayersPerNote = 240;
    private const int MaxLayerSnapshotLength = 8000;
    private const int MaxVoiceSessionsPerNote = 120;
    private const int MaxVoiceEventsPerSession = 240;
    private const int MaxVoiceEventTextLength = 1200;
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
    private readonly Dictionary<string, InsightCacheEntry> _insightCache = new(StringComparer.Ordinal);

    private string _activeNoteId = string.Empty;
    private string _searchText = string.Empty;
    private string _autocompleteSuggestion = string.Empty;
    private string _activeWord = string.Empty;
    private string _statusMessage = "Local-first mode";
    private string _lastAiSource = "fallback";
    private string _previewTheme = DefaultPreviewTheme;
    private bool _isBusy;
    private bool _isInitializing = true;
    private bool _isRefreshingInsights;
    private bool _isApplyingSlash;
    private int _voiceProcessingCount;
    private VoiceInterimRange? _voiceInterimRange;
    private long _insightRefreshGeneration;
    private string _activeInsightRefreshKey = string.Empty;

    private bool _isPaletteOpen;
    private string _paletteQuery = string.Empty;
    private int _paletteIndex;
    private ElementReference _paletteInput;
    private ElementReference _editorInput;

    private bool _isVoiceRecording;
    private bool _isVoiceSupported = true;
    private string? _activeVoiceSessionId;
    private string? _activeVoiceSessionNoteId;
    private DotNetObjectReference<Home>? _dotNetRef;

    private CancellationTokenSource? _streamCts;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _voiceAuditPersistCts;

    private IEnumerable<NoteDocument> FilteredNotes =>
        _notes.Where(note =>
            string.IsNullOrWhiteSpace(_searchText) ||
            (note.Title?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (note.Content?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false));

    private NoteDocument? ActiveNote => _notes.FirstOrDefault(note => note.Id == _activeNoteId);
    private string ActiveWord => _activeWord;
    private int ActiveLayerCount => ActiveNote?.Layers?.Count ?? 0;

    private List<PaletteCommand> PaletteResults => GetPaletteResults();
    private IReadOnlyList<PreviewThemeOption> AvailablePreviewThemes => PreviewThemes;
    private bool IsVoiceProcessing => _voiceProcessingCount > 0;
    private bool ShowEditorActivity => _isBusy || _isApplyingSlash;
    private bool ShowWorkspaceSkeleton => _isInitializing;
    private bool ShowInsightsSkeleton => _isInitializing || _isRefreshingInsights;
    private string? WorkingLabel =>
        _isInitializing ? "Loading workspace..." :
        _isBusy ? "Generating continuation..." :
        _isApplyingSlash ? "Applying slash command..." :
        IsVoiceProcessing ? "Refining transcript..." :
        _isRefreshingInsights ? "Refreshing insights..." :
        null;
    private string CurrentPluginInsightSignature => string.Join('|', _pluginPanels
        .OrderBy(panel => panel.Id, StringComparer.OrdinalIgnoreCase)
        .Select(panel => $"{panel.Id}:{panel.Mode}:{panel.Prompt}"));

    private List<PanelView> AllPanels =>
    [
        new PanelView("Hints", "Live structure and clarity nudges.", _hints),
        new PanelView("Help", "Editing and markdown command assist.", _help),
        new PanelView("Brainstorm", "Prompted ideas while you write.", _brainstorm),
        new PanelView("History QC", "Version, diff, and tone checkpoints.", BuildHistoryPanelItems()),
        new PanelView("Voice Review", "Recording and transcription audit trail.", BuildVoiceReviewPanelItems()),
        .. _pluginPanels.Select(panel => new PanelView(panel.Title, panel.Description, GetPluginPanelItems(panel.Id)))
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await LoadOrInitializeAsync();
            await LoadPluginManifestAsync();
            await DetectVoiceSupportAsync();
            _ = StartInitialInsightsRefreshAsync();
        }
        catch
        {
            _statusMessage = "Workspace loaded with fallback defaults";
        }
        finally
        {
            _isInitializing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task StartInitialInsightsRefreshAsync()
    {
        try
        {
            await RefreshAssistantPanelsAsync();
        }
        catch
        {
            _isRefreshingInsights = false;
            _activeInsightRefreshKey = string.Empty;
            _statusMessage = "Workspace ready; insights unavailable";
            await InvokeAsync(StateHasChanged);
        }
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
                var normalizedNotes = NormalizeLoadedNotes(state?.Notes);
                if (normalizedNotes.Count > 0)
                {
                    _notes.Clear();
                    _notes.AddRange(normalizedNotes);
                    EnsureLayerHistory();
                    _activeNoteId = state?.ActiveNoteId ?? _notes[0].Id;
                    if (_notes.All(note => note.Id != _activeNoteId))
                    {
                        _activeNoteId = _notes[0].Id;
                    }
                    _previewTheme = NormalizePreviewTheme(state?.PreviewTheme);
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
        _insightCache.Clear();

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
        CancelPendingInsightWork();
        ClearVoiceInterimTracking();
        _activeNoteId = noteId;
        UpdateEditorSignals();
        if (!TryApplyCachedInsightsForActiveNote())
        {
            QueueAssistantRefresh();
        }
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
    }

    private void CancelPendingInsightWork()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;

        _isRefreshingInsights = false;
        _activeInsightRefreshKey = string.Empty;
    }

    private void ClearVoiceInterimTracking(string? noteId = null)
    {
        if (_voiceInterimRange is null)
        {
            return;
        }

        if (noteId is null || string.Equals(_voiceInterimRange.NoteId, noteId, StringComparison.Ordinal))
        {
            _voiceInterimRange = null;
        }
    }

    private async Task HandleDraftInput(ChangeEventArgs args)
    {
        if (ActiveNote is null)
        {
            return;
        }

        ClearVoiceInterimTracking(ActiveNote.Id);
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
        if (ActiveNote is null || _isBusy || _isApplyingSlash)
        {
            return;
        }

        _isBusy = true;
        _statusMessage = "Generating continuation...";
        var before = ActiveNote.Content;
        StateHasChanged();

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
            StateHasChanged();
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
        ClearVoiceInterimTracking();

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
            if (ActiveNote is not null)
            {
                StartVoiceSessionAudit(ActiveNote, "browser-speech", "capture-recording");
            }
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

        ClearVoiceInterimTracking();
        _isVoiceRecording = false;
        _statusMessage = statusMessage;
        CompleteActiveVoiceSession("stopped", "browser-speech", statusMessage);
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnVoiceCaptureStatusChanged(string status)
    {
        return InvokeAsync(() =>
        {
            if (string.Equals(status, "recording", StringComparison.OrdinalIgnoreCase))
            {
                ClearVoiceInterimTracking();
                _isVoiceRecording = true;
                _statusMessage = "Voice capture active";
                if (ActiveNote is not null)
                {
                    StartVoiceSessionAudit(ActiveNote, "browser-speech", "capture-recording");
                }
            }
            else if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                ClearVoiceInterimTracking();
                _isVoiceRecording = false;
                CompleteActiveVoiceSession("stopped", "browser-speech", "capture-stopped");
                if (!string.Equals(_statusMessage, "Voice transcript cleaned", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_statusMessage, "Voice transcript inserted", StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = "Voice capture stopped";
                }
            }
            else if (status.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                ClearVoiceInterimTracking();
                _isVoiceRecording = false;
                _statusMessage = "Voice capture error";
                CompleteActiveVoiceSession("error", "browser-speech", status);
            }

            StateHasChanged();
        });
    }

    [JSInvokable]
    public async Task OnVoiceTranscriptInterim(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        var normalizedTranscript = transcript.Trim();
        await InvokeAsync(async () =>
        {
            if (ActiveNote is null)
            {
                return;
            }

            await _voicePipelineLock.WaitAsync();
            try
            {
                AppendVoiceAuditEvent(ActiveNote, "transcript-interim", normalizedTranscript, "browser-speech", detail: "interim", dedupeWithPrevious: true);
                await ApplyVoiceInterimTranscriptAsync(ActiveNote, normalizedTranscript);
            }
            finally
            {
                _voicePipelineLock.Release();
            }
        });
    }

    [JSInvokable]
    public async Task OnVoiceTranscriptFinalized(string transcript)
    {
        var normalizedTranscript = transcript?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return;
        }

        await InvokeAsync(async () =>
        {
            if (ActiveNote is null)
            {
                return;
            }

            VoiceCleanupRequest? cleanupRequest;
            await _voicePipelineLock.WaitAsync();
            try
            {
                AppendVoiceAuditEvent(ActiveNote, "transcript-finalized", normalizedTranscript, "browser-speech", detail: "finalized");
                cleanupRequest = await CommitFinalVoiceTranscriptAsync(ActiveNote, normalizedTranscript);
            }
            finally
            {
                _voicePipelineLock.Release();
            }

            if (cleanupRequest is not null)
            {
                QueueVoiceCleanup(cleanupRequest);
            }
        });
    }

    private async Task ApplyVoiceInterimTranscriptAsync(NoteDocument note, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        var insertion = await InsertVoiceTextAtCursorAsync(note, transcript);
        if (insertion is null || string.IsNullOrWhiteSpace(insertion.Value))
        {
            return;
        }

        note.Content = insertion.Value;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        _voiceInterimRange = new VoiceInterimRange(note.Id, insertion.Start, insertion.End);
        _statusMessage = "Transcribing live...";
        UpdateEditorSignals();
        StateHasChanged();
    }

    private async Task<VoiceCleanupRequest?> CommitFinalVoiceTranscriptAsync(NoteDocument note, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        var before = note.Content;
        var insertion = await InsertVoiceTextAtCursorAsync(note, transcript);
        ClearVoiceInterimTracking(note.Id);

        if (insertion is null || string.IsNullOrWhiteSpace(insertion.Value))
        {
            return null;
        }

        note.Content = insertion.Value;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        _statusMessage = "Voice transcript inserted";
        AppendVoiceAuditEvent(
            note,
            "transcript-inserted",
            transcript,
            "browser-speech",
            rangeStart: insertion.Start,
            rangeEnd: insertion.End,
            detail: "inserted-at-cursor");

        RecordLayer(
            note,
            operation: "voice-transcript",
            source: "browser-speech",
            contentBefore: before,
            contentAfter: note.Content,
            titleBefore: note.Title,
            titleAfter: note.Title,
            annotation: transcript);

        UpdateEditorSignals();
        await PersistStateAsync();
        QueueAssistantRefresh();
        StateHasChanged();

        var cleanupSegment = SafeSlice(note.Content, insertion.Start, insertion.End);
        if (insertion.Start < 0 || insertion.End <= insertion.Start || string.IsNullOrWhiteSpace(cleanupSegment))
        {
            AppendVoiceAuditEvent(
                note,
                "cleanup-skipped",
                cleanupSegment,
                "fallback",
                rangeStart: insertion.Start,
                rangeEnd: insertion.End,
                detail: "invalid-insertion-range");
            RecordLayer(
                note,
                operation: "voice-cleanup-attempt",
                source: "fallback",
                contentBefore: note.Content,
                contentAfter: note.Content,
                titleBefore: note.Title,
                titleAfter: note.Title,
                annotation: "Cleanup skipped; insertion range unavailable");
            await PersistStateAsync();
            return null;
        }

        return new VoiceCleanupRequest(note, insertion.Start, insertion.End, cleanupSegment, transcript);
    }

    private async Task<EditorInsertionResult?> InsertVoiceTextAtCursorAsync(NoteDocument note, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        EditorInsertionResult? insertion = null;
        var options = new { dispatchInput = false };
        var activeRange = _voiceInterimRange is not null &&
            string.Equals(_voiceInterimRange.NoteId, note.Id, StringComparison.Ordinal)
            ? _voiceInterimRange
            : null;

        try
        {
            insertion = activeRange is null
                ? await JS.InvokeAsync<EditorInsertionResult>(
                    "JDWriterStudio.insertTextAtCursor",
                    _editorInput,
                    transcript,
                    options)
                : await JS.InvokeAsync<EditorInsertionResult>(
                    "JDWriterStudio.replaceTextRange",
                    _editorInput,
                    activeRange.Start,
                    activeRange.End,
                    transcript,
                    options);
        }
        catch
        {
            // Fallback paths are handled below.
        }

        if (insertion is not null && !string.IsNullOrWhiteSpace(insertion.Value))
        {
            return insertion;
        }

        if (activeRange is not null)
        {
            var replaced = ReplaceRange(note.Content, activeRange.Start, activeRange.End, transcript);
            var fallbackStart = Math.Clamp(activeRange.Start, 0, replaced.Length);
            var fallbackEnd = Math.Clamp(fallbackStart + transcript.Length, fallbackStart, replaced.Length);
            return new EditorInsertionResult
            {
                Value = replaced,
                Start = fallbackStart,
                End = fallbackEnd
            };
        }

        var appended = AppendToDraft(note.Content, transcript);
        var appendedEnd = appended.Length;
        var appendedStart = Math.Max(0, appended.LastIndexOf(transcript, StringComparison.Ordinal));
        return new EditorInsertionResult
        {
            Value = appended,
            Start = appendedStart,
            End = appendedEnd
        };
    }

    private void QueueVoiceCleanup(VoiceCleanupRequest request)
    {
        _ = InvokeAsync(async () =>
        {
            AppendVoiceAuditEvent(
                request.Note,
                "cleanup-queued",
                request.InsertedSegment,
                "ai",
                rangeStart: request.Start,
                rangeEnd: request.End,
                detail: "awaiting-ai-cleanup");
            _voiceProcessingCount++;
            _statusMessage = "Refining voice transcript...";
            StateHasChanged();

            try
            {
                await ApplyAiVoiceCleanupAsync(request.Note, request.Start, request.End, request.InsertedSegment, request.RawTranscript);
            }
            finally
            {
                _voiceProcessingCount = Math.Max(0, _voiceProcessingCount - 1);
                if (_voiceProcessingCount == 0 &&
                    string.Equals(_statusMessage, "Refining voice transcript...", StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = "Voice transcript inserted";
                }

                StateHasChanged();
            }
        });
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
                AppendVoiceAuditEvent(
                    note,
                    "cleanup-noop",
                    cleaned ?? string.Empty,
                    response.Source,
                    rangeStart: rangeStart,
                    rangeEnd: rangeEnd,
                    detail: "cleanup-no-material-change");
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
                AppendVoiceAuditEvent(
                    note,
                    "cleanup-skipped",
                    insertedSegment,
                    response.Source,
                    rangeStart: rangeStart,
                    rangeEnd: rangeEnd,
                    detail: "concurrent-edits-detected");
                return;
            }

            var before = note.Content;
            note.Content = ReplaceRange(note.Content, rangeStart, rangeEnd, cleaned!);
            note.UpdatedAt = DateTimeOffset.UtcNow;
            _statusMessage = "Voice transcript cleaned";
            _lastAiSource = response.Source;
            AppendVoiceAuditEvent(
                note,
                "cleanup-applied",
                cleaned,
                response.Source,
                rangeStart: rangeStart,
                rangeEnd: Math.Clamp(rangeStart + cleaned!.Length, rangeStart, note.Content.Length),
                detail: "ai-cleanup-applied");

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
            AppendVoiceAuditEvent(
                note,
                "cleanup-failed",
                insertedSegment,
                "fallback",
                rangeStart: rangeStart,
                rangeEnd: rangeEnd,
                detail: "cleanup-exception");
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
        if (ActiveNote is null || _isApplyingSlash)
        {
            return;
        }

        _isApplyingSlash = true;
        _statusMessage = $"Applying /{command.Name}...";
        StateHasChanged();

        try
        {
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
                _statusMessage = $"Slash /{command.Name} returned no output";
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
        catch
        {
            _statusMessage = $"Slash /{command.Name} unavailable";
        }
        finally
        {
            _isApplyingSlash = false;
            StateHasChanged();
        }
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
            await RefreshAssistantPanelsAsync(force: false);
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled debounce.
        }
    }

    private async Task RefreshAssistantPanelsAsync(bool force = true)
    {
        if (ActiveNote is null)
        {
            return;
        }

        var noteId = ActiveNote.Id;
        var contentSignature = BuildInsightContentSignature(ActiveNote.Content);
        var refreshKey = $"{noteId}:{contentSignature}:{CurrentPluginInsightSignature}";

        if (!force && TryApplyCachedInsights(noteId, contentSignature))
        {
            _isRefreshingInsights = false;
            _statusMessage = "Insights restored from cache";
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (_isRefreshingInsights &&
            string.Equals(_activeInsightRefreshKey, refreshKey, StringComparison.Ordinal))
        {
            return;
        }

        var refreshGeneration = Interlocked.Increment(ref _insightRefreshGeneration);
        _isRefreshingInsights = true;
        _activeInsightRefreshKey = refreshKey;
        await InvokeAsync(StateHasChanged);

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
            if (!token.IsCancellationRequested)
            {
                CacheInsights(noteId, contentSignature);
                _statusMessage = "Insights updated";
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled refresh.
        }
        finally
        {
            if (refreshGeneration == _insightRefreshGeneration)
            {
                _isRefreshingInsights = false;
                _activeInsightRefreshKey = string.Empty;
                await InvokeAsync(StateHasChanged);
            }
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

    private bool TryApplyCachedInsightsForActiveNote()
    {
        if (ActiveNote is null)
        {
            return false;
        }

        var contentSignature = BuildInsightContentSignature(ActiveNote.Content);
        return TryApplyCachedInsights(ActiveNote.Id, contentSignature);
    }

    private bool TryApplyCachedInsights(string noteId, string contentSignature)
    {
        if (!_insightCache.TryGetValue(noteId, out var cached))
        {
            return false;
        }

        if (!string.Equals(cached.ContentSignature, contentSignature, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(cached.PluginSignature, CurrentPluginInsightSignature, StringComparison.Ordinal))
        {
            return false;
        }

        _hints.Clear();
        _hints.AddRange(cached.Hints);

        _help.Clear();
        _help.AddRange(cached.Help);

        _brainstorm.Clear();
        _brainstorm.AddRange(cached.Brainstorm);

        foreach (var panel in _pluginPanels)
        {
            var target = GetPluginPanelItems(panel.Id);
            target.Clear();
            if (cached.PluginPanels.TryGetValue(panel.Id, out var panelItems))
            {
                target.AddRange(panelItems);
            }
        }

        return true;
    }

    private void CacheInsights(string noteId, string contentSignature)
    {
        var pluginPanels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in _pluginPanels)
        {
            pluginPanels[panel.Id] = [.. GetPluginPanelItems(panel.Id)];
        }

        _insightCache[noteId] = new InsightCacheEntry
        {
            ContentSignature = contentSignature,
            PluginSignature = CurrentPluginInsightSignature,
            Hints = [.. _hints],
            Help = [.. _help],
            Brainstorm = [.. _brainstorm],
            PluginPanels = pluginPanels
        };
    }

    private static string BuildInsightContentSignature(string content)
    {
        var normalized = content ?? string.Empty;
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

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

        var layers = (ActiveNote.Layers ?? [])
            .Where(layer => layer is not null)
            .ToList();
        if (layers.Count == 0)
        {
            return ["No history layers captured yet."];
        }

        var recent = layers
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

        if (layers.Count >= 2)
        {
            var newest = layers[^1].Tone?.Sentiment ?? 0;
            var previous = layers[^2].Tone?.Sentiment ?? 0;
            var drift = Math.Abs(newest - previous);
            recent.Insert(0, $"Tone drift vs previous layer: {drift:0.00}");
        }

        return recent;
    }

    private List<string> BuildVoiceReviewPanelItems()
    {
        if (ActiveNote is null)
        {
            return ["No active note selected."];
        }

        var sessions = (ActiveNote.VoiceSessions ?? [])
            .Where(session => session is not null)
            .OrderByDescending(session => session.StartedAt)
            .Take(8)
            .ToList();

        if (sessions.Count == 0)
        {
            return ["No voice sessions captured yet."];
        }

        var items = new List<string>();
        foreach (var session in sessions)
        {
            var status = string.IsNullOrWhiteSpace(session.Status) ? "unknown" : session.Status;
            var sessionEventCount = session.Events?.Count ?? 0;
            var duration = session.EndedAt is null
                ? "live"
                : $"{Math.Max(0, (session.EndedAt.Value - session.StartedAt).TotalSeconds):0.0}s";

            items.Add($"{session.StartedAt.ToLocalTime():MMM d h:mm:ss tt} | {status} | {sessionEventCount} events | {duration}");

            var recentEvents = (session.Events ?? [])
                .Where(evt => evt is not null)
                .OrderByDescending(evt => evt.At)
                .Take(4)
                .ToList();

            foreach (var evt in recentEvents)
            {
                var text = string.IsNullOrWhiteSpace(evt.Text)
                    ? string.Empty
                    : $" | \"{ToPanelSnippet(evt.Text, 92)}\"";
                var detail = string.IsNullOrWhiteSpace(evt.Detail)
                    ? string.Empty
                    : $" ({evt.Detail})";
                items.Add($"- {evt.At.ToLocalTime():h:mm:ss tt} | {evt.Kind}{detail}{text}");
            }

            if (sessionEventCount > recentEvents.Count)
            {
                items.Add($"- ... {sessionEventCount - recentEvents.Count} earlier events");
            }
        }

        return items;
    }

    private void StartVoiceSessionAudit(NoteDocument note, string source, string detail)
    {
        var session = EnsureActiveVoiceSession(note, source);
        if (session is null)
        {
            return;
        }

        session.Status = "recording";
        session.EndedAt = null;
        AppendVoiceAuditEvent(note, "capture-status", "recording", source, detail: detail, dedupeWithPrevious: true);
    }

    private void CompleteActiveVoiceSession(string status, string source, string? detail)
    {
        var note = ResolveVoiceSessionNote();
        if (note is null)
        {
            _activeVoiceSessionId = null;
            _activeVoiceSessionNoteId = null;
            return;
        }

        var session = TryGetActiveVoiceSession(note);
        if (session is null)
        {
            _activeVoiceSessionId = null;
            _activeVoiceSessionNoteId = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        session.Status = NormalizeVoiceSessionStatus(status);
        session.EndedAt = now;
        AppendVoiceAuditEvent(note, "capture-status", session.Status, source, detail: detail, dedupeWithPrevious: true);
        QueueVoiceAuditPersist();
        _activeVoiceSessionId = null;
        _activeVoiceSessionNoteId = null;
    }

    private NoteDocument? ResolveVoiceSessionNote()
    {
        if (!string.IsNullOrWhiteSpace(_activeVoiceSessionNoteId))
        {
            return _notes.FirstOrDefault(note => string.Equals(note.Id, _activeVoiceSessionNoteId, StringComparison.Ordinal));
        }

        return ActiveNote;
    }

    private VoiceSessionAudit? TryGetActiveVoiceSession(NoteDocument note)
    {
        if (string.IsNullOrWhiteSpace(_activeVoiceSessionId))
        {
            return null;
        }

        EnsureVoiceAuditHistory(note);
        return note.VoiceSessions.FirstOrDefault(session => string.Equals(session.SessionId, _activeVoiceSessionId, StringComparison.Ordinal));
    }

    private VoiceSessionAudit? EnsureActiveVoiceSession(NoteDocument note, string source)
    {
        EnsureVoiceAuditHistory(note);

        if (!string.IsNullOrWhiteSpace(_activeVoiceSessionNoteId) &&
            !string.Equals(_activeVoiceSessionNoteId, note.Id, StringComparison.Ordinal))
        {
            _activeVoiceSessionId = null;
        }

        _activeVoiceSessionNoteId = note.Id;

        var existing = TryGetActiveVoiceSession(note);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var session = new VoiceSessionAudit
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StartedAt = now,
            EndedAt = null,
            Status = "recording",
            Source = string.IsNullOrWhiteSpace(source) ? "browser-speech" : source.Trim()
        };

        note.VoiceSessions.Add(session);
        TrimVoiceSessionHistory(note.VoiceSessions);
        _activeVoiceSessionId = session.SessionId;
        QueueVoiceAuditPersist();
        return session;
    }

    private void AppendVoiceAuditEvent(
        NoteDocument note,
        string kind,
        string? text,
        string source,
        int? rangeStart = null,
        int? rangeEnd = null,
        string? detail = null,
        bool dedupeWithPrevious = false)
    {
        var session = EnsureActiveVoiceSession(note, source);
        if (session is null)
        {
            return;
        }

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "event" : kind.Trim();
        var normalizedText = NormalizeVoiceEventText(text);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "browser-speech" : source.Trim();
        var normalizedDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();

        if (dedupeWithPrevious && session.Events.Count > 0)
        {
            var previous = session.Events[^1];
            if (string.Equals(previous.Kind, normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Text, normalizedText, StringComparison.Ordinal) &&
                string.Equals(previous.Source, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Detail, normalizedDetail, StringComparison.Ordinal) &&
                previous.RangeStart == rangeStart &&
                previous.RangeEnd == rangeEnd)
            {
                previous.At = DateTimeOffset.UtcNow;
                QueueVoiceAuditPersist();
                return;
            }
        }

        session.Events.Add(new VoiceAuditEvent
        {
            At = DateTimeOffset.UtcNow,
            Kind = normalizedKind,
            Text = normalizedText,
            Source = normalizedSource,
            Detail = normalizedDetail,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd
        });

        session.Events = session.Events
            .OrderBy(evt => evt.At)
            .TakeLast(MaxVoiceEventsPerSession)
            .ToList();
        QueueVoiceAuditPersist();
    }

    private void QueueVoiceAuditPersist()
    {
        _voiceAuditPersistCts?.Cancel();
        _voiceAuditPersistCts?.Dispose();
        _voiceAuditPersistCts = new CancellationTokenSource();

        var token = _voiceAuditPersistCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await InvokeAsync(async () =>
                {
                    try
                    {
                        await PersistStateAsync();
                    }
                    catch
                    {
                        // Ignore transient persistence failures during high-frequency voice updates.
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation when a new voice event supersedes this persist request.
            }
        }, CancellationToken.None);
    }

    private void EnsureLayerHistory()
    {
        foreach (var note in _notes)
        {
            note.Id = string.IsNullOrWhiteSpace(note.Id) ? Guid.NewGuid().ToString("N") : note.Id.Trim();
            note.Title = string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title.Trim();
            note.Content ??= string.Empty;
            note.Layers = (note.Layers ?? [])
                .Where(layer => layer is not null)
                .ToList();
            if (note.UpdatedAt == default)
            {
                note.UpdatedAt = DateTimeOffset.UtcNow;
            }
            EnsureVoiceAuditHistory(note);

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

    private static List<NoteDocument> NormalizeLoadedNotes(List<NoteDocument>? notes)
    {
        if (notes is null || notes.Count == 0)
        {
            return [];
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<NoteDocument>(notes.Count);

        foreach (var rawNote in notes)
        {
            if (rawNote is null)
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(rawNote.Id) ? Guid.NewGuid().ToString("N") : rawNote.Id.Trim();
            if (!seenIds.Add(id))
            {
                id = Guid.NewGuid().ToString("N");
                seenIds.Add(id);
            }

            var title = string.IsNullOrWhiteSpace(rawNote.Title) ? "Untitled note" : rawNote.Title.Trim();
            var content = rawNote.Content ?? string.Empty;
            var updatedAt = rawNote.UpdatedAt == default ? DateTimeOffset.UtcNow : rawNote.UpdatedAt;
            var sanitizedLayers = new List<NoteLayer>();
            foreach (var layer in rawNote.Layers ?? [])
            {
                if (layer is null)
                {
                    continue;
                }

                layer.LayerId = string.IsNullOrWhiteSpace(layer.LayerId) ? Guid.NewGuid().ToString("N") : layer.LayerId.Trim();
                layer.Operation = string.IsNullOrWhiteSpace(layer.Operation) ? "manual-edit" : layer.Operation.Trim();
                layer.Source = string.IsNullOrWhiteSpace(layer.Source) ? "local" : layer.Source.Trim();
                layer.TitleBefore ??= title;
                layer.TitleAfter ??= title;
                layer.Snapshot ??= CaptureSnapshot(content);
                layer.CreatedAt = layer.CreatedAt == default ? updatedAt : layer.CreatedAt;
                layer.UpdatedAt = layer.UpdatedAt == default ? layer.CreatedAt : layer.UpdatedAt;
                sanitizedLayers.Add(layer);
            }

            var sanitizedVoiceSessions = NormalizeVoiceSessions(rawNote.VoiceSessions, updatedAt);

            normalized.Add(new NoteDocument
            {
                Id = id,
                Title = title,
                Content = content,
                UpdatedAt = updatedAt,
                Layers = sanitizedLayers,
                VoiceSessions = sanitizedVoiceSessions
            });
        }

        return normalized;
    }

    private static List<VoiceSessionAudit> NormalizeVoiceSessions(List<VoiceSessionAudit>? sessions, DateTimeOffset fallbackTimestamp)
    {
        var sanitized = new List<VoiceSessionAudit>();
        foreach (var rawSession in sessions ?? [])
        {
            if (rawSession is null)
            {
                continue;
            }

            var startedAt = rawSession.StartedAt == default ? fallbackTimestamp : rawSession.StartedAt;
            var endedAt = rawSession.EndedAt;
            if (endedAt is not null && endedAt.Value < startedAt)
            {
                endedAt = startedAt;
            }

            var normalizedEvents = new List<VoiceAuditEvent>();
            foreach (var rawEvent in rawSession.Events ?? [])
            {
                if (rawEvent is null)
                {
                    continue;
                }

                normalizedEvents.Add(new VoiceAuditEvent
                {
                    At = rawEvent.At == default ? startedAt : rawEvent.At,
                    Kind = string.IsNullOrWhiteSpace(rawEvent.Kind) ? "event" : rawEvent.Kind.Trim(),
                    Source = string.IsNullOrWhiteSpace(rawEvent.Source) ? "browser-speech" : rawEvent.Source.Trim(),
                    Text = NormalizeVoiceEventText(rawEvent.Text),
                    Detail = string.IsNullOrWhiteSpace(rawEvent.Detail) ? null : rawEvent.Detail.Trim(),
                    RangeStart = rawEvent.RangeStart,
                    RangeEnd = rawEvent.RangeEnd
                });
            }

            normalizedEvents = normalizedEvents
                .OrderBy(evt => evt.At)
                .TakeLast(MaxVoiceEventsPerSession)
                .ToList();

            sanitized.Add(new VoiceSessionAudit
            {
                SessionId = string.IsNullOrWhiteSpace(rawSession.SessionId) ? Guid.NewGuid().ToString("N") : rawSession.SessionId.Trim(),
                StartedAt = startedAt,
                EndedAt = endedAt,
                Status = NormalizeVoiceSessionStatus(rawSession.Status),
                Source = string.IsNullOrWhiteSpace(rawSession.Source) ? "browser-speech" : rawSession.Source.Trim(),
                Events = normalizedEvents
            });
        }

        return sanitized
            .OrderBy(session => session.StartedAt)
            .TakeLast(MaxVoiceSessionsPerNote)
            .ToList();
    }

    private void EnsureVoiceAuditHistory(NoteDocument note)
    {
        note.VoiceSessions = NormalizeVoiceSessions(note.VoiceSessions, note.UpdatedAt);
        TrimVoiceSessionHistory(note.VoiceSessions);
    }

    private static void TrimVoiceSessionHistory(List<VoiceSessionAudit> sessions)
    {
        if (sessions.Count <= MaxVoiceSessionsPerNote)
        {
            return;
        }

        sessions.RemoveRange(0, sessions.Count - MaxVoiceSessionsPerNote);
    }

    private static string NormalizeVoiceSessionStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "recording";
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "recording" => "recording",
            "stopped" => "stopped",
            "completed" => "completed",
            "error" => "error",
            _ => normalized
        };
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

    private static string NormalizeVoiceEventText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= MaxVoiceEventTextLength
            ? normalized
            : normalized[..MaxVoiceEventTextLength] + "...";
    }

    private static string ToPanelSnippet(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeVoiceEventText(text);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
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

        _voiceAuditPersistCts?.Cancel();
        _voiceAuditPersistCts?.Dispose();

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
        public List<VoiceSessionAudit> VoiceSessions { get; set; } = [];

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

    private sealed class InsightCacheEntry
    {
        public string ContentSignature { get; set; } = string.Empty;
        public string PluginSignature { get; set; } = string.Empty;
        public List<string> Hints { get; set; } = [];
        public List<string> Help { get; set; } = [];
        public List<string> Brainstorm { get; set; } = [];
        public Dictionary<string, List<string>> PluginPanels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    private sealed class VoiceSessionAudit
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public string Status { get; set; } = "recording";
        public string Source { get; set; } = "browser-speech";
        public List<VoiceAuditEvent> Events { get; set; } = [];
    }

    private sealed class VoiceAuditEvent
    {
        public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
        public string Kind { get; set; } = "event";
        public string Text { get; set; } = string.Empty;
        public string Source { get; set; } = "browser-speech";
        public string? Detail { get; set; }
        public int? RangeStart { get; set; }
        public int? RangeEnd { get; set; }
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

    private sealed record VoiceInterimRange(string NoteId, int Start, int End);
    private sealed record VoiceCleanupRequest(NoteDocument Note, int Start, int End, string InsertedSegment, string RawTranscript);

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

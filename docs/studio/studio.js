(() => {
  "use strict";

  const STORAGE_KEY = "jdwriter.pages.studio.v2";
  const INSIGHT_DEBOUNCE_MS = 900;
  const MAX_NOTES = 120;
  const MAX_VOICE_SESSIONS = 18;
  const MAX_VOICE_EVENTS = 24;
  const WEBLLM_IMPORT_CANDIDATES = [
    "https://esm.run/@mlc-ai/web-llm",
    "https://cdn.jsdelivr.net/npm/@mlc-ai/web-llm/+esm"
  ];

  const state = {
    notes: [],
    activeId: "",
    query: "",
    previewTheme: "studio",
    lastSource: "local",
    slashContext: null,
    paletteOpen: false,
    paletteQuery: "",
    paletteIndex: 0,
    autocompleteSuffix: "",
    activeWord: "",
    insights: {
      hints: [],
      help: [],
      brainstorm: []
    },
    ai: {
      selectedModel: "Llama-3.2-3B-Instruct-q4f32_1-MLC",
      module: null,
      engine: null,
      ready: false,
      loading: false,
      progress: "",
      error: "",
      insightRequestId: 0
    },
    voice: {
      supported: false,
      active: false,
      recognition: null,
      interimRange: null,
      activeSession: null,
      sessions: [],
      settings: {
        language: "en-US",
        rawMode: true,
        aiCleanup: false
      }
    },
    busy: {
      continue: false,
      insights: false
    }
  };

  const el = {
    noteCount: document.getElementById("note-count"),
    sourcePill: document.getElementById("source-pill"),
    modelPill: document.getElementById("model-pill"),
    voicePill: document.getElementById("voice-pill"),
    search: document.getElementById("search-notes"),
    newNote: document.getElementById("new-note"),
    noteList: document.getElementById("note-list"),
    title: document.getElementById("note-title"),
    content: document.getElementById("note-content"),
    preview: document.getElementById("preview"),
    previewTheme: document.getElementById("preview-theme"),
    continueButton: document.getElementById("continue-button"),
    exportButton: document.getElementById("export-button"),
    insightsButton: document.getElementById("insights-button"),
    voiceButton: document.getElementById("voice-button"),
    paletteButton: document.getElementById("palette-button"),
    hintsList: document.getElementById("hints-list"),
    helpList: document.getElementById("help-list"),
    brainstormList: document.getElementById("brainstorm-list"),
    voiceReviewList: document.getElementById("voice-review-list"),
    modelStatusDetail: document.getElementById("model-status-detail"),
    modelDiagnostics: document.getElementById("model-diagnostics"),
    runtimeStatus: document.getElementById("runtime-status"),
    modelSelect: document.getElementById("model-select"),
    modelLoadButton: document.getElementById("model-load-button"),
    voiceRawModeToggle: document.getElementById("voice-raw-mode-toggle"),
    voiceAiCleanupToggle: document.getElementById("voice-ai-cleanup-toggle"),
    voiceLanguage: document.getElementById("voice-language"),
    autocompleteChip: document.getElementById("autocomplete-chip"),
    slashBar: document.getElementById("slash-bar"),
    paletteBackdrop: document.getElementById("palette-backdrop"),
    palette: document.getElementById("palette"),
    paletteInput: document.getElementById("palette-input"),
    paletteList: document.getElementById("palette-list")
  };

  const slashCommands = [
    { name: "summarize", description: "Condense the draft into key bullets.", run: summarizeDraft },
    { name: "outline", description: "Generate a sectioned markdown outline.", run: outlineDraft },
    { name: "action-items", description: "Extract concrete checkbox tasks.", run: actionItemsDraft }
  ];

  let insightsTimer = 0;

  init();

  function init() {
    wireEvents();
    detectVoiceSupport();
    loadState();
    ensureActiveNote();
    renderAll();
    refreshInsights({ preferModel: false });
  }

  function wireEvents() {
    el.search.addEventListener("input", () => {
      state.query = (el.search.value || "").trim();
      renderNoteList();
    });

    el.newNote.addEventListener("click", () => {
      createNote();
      renderAll();
      persistState();
      refreshInsights({ preferModel: false });
    });

    el.title.addEventListener("input", () => {
      const note = getActiveNote();
      if (!note) {
        return;
      }

      note.title = (el.title.value || "").trim() || "Untitled note";
      note.updatedAt = new Date().toISOString();
      renderNoteList();
      persistState();
    });

    el.content.addEventListener("input", () => {
      const note = getActiveNote();
      if (!note) {
        return;
      }

      note.content = el.content.value || "";
      note.updatedAt = new Date().toISOString();
      renderPreview();
      renderNoteList();
      updateAutocomplete();
      renderSlashSuggestions();
      persistState();
      queueInsightsRefresh();
    });

    el.content.addEventListener("click", () => {
      updateAutocomplete();
      renderSlashSuggestions();
    });

    el.content.addEventListener("keyup", () => {
      updateAutocomplete();
      renderSlashSuggestions();
    });

    el.content.addEventListener("keydown", (event) => {
      if (event.ctrlKey && event.key.toLowerCase() === "j") {
        event.preventDefault();
        void continueDraft();
        return;
      }

      if (event.ctrlKey && event.key.toLowerCase() === "m") {
        event.preventDefault();
        void toggleVoiceCapture();
        return;
      }

      if (event.ctrlKey && event.code === "Space") {
        event.preventDefault();
        acceptAutocomplete();
        return;
      }

      if (event.key === "Tab" && state.slashContext) {
        const first = slashCommands.find((cmd) => cmd.name.startsWith(state.slashContext.token));
        if (first) {
          event.preventDefault();
          void applySlashCommand(first);
        }
      }
    });

    el.continueButton.addEventListener("click", () => {
      void continueDraft();
    });

    el.exportButton.addEventListener("click", exportActiveNote);

    el.insightsButton.addEventListener("click", () => {
      void refreshInsights({ preferModel: true });
    });

    el.voiceButton.addEventListener("click", () => {
      void toggleVoiceCapture();
    });

    el.paletteButton.addEventListener("click", openPalette);

    el.previewTheme.addEventListener("change", () => {
      state.previewTheme = normalizeTheme(el.previewTheme.value);
      renderPreview();
      persistState();
    });

    el.autocompleteChip.addEventListener("click", acceptAutocomplete);

    el.modelSelect.addEventListener("change", () => {
      state.ai.selectedModel = el.modelSelect.value || state.ai.selectedModel;
      persistState();
      renderRuntimeStatus();
    });

    el.modelLoadButton.addEventListener("click", () => {
      void toggleModelRuntime();
    });

    el.voiceRawModeToggle.addEventListener("change", () => {
      state.voice.settings.rawMode = Boolean(el.voiceRawModeToggle.checked);
      persistState();
      renderRuntimeStatus();
    });

    el.voiceAiCleanupToggle.addEventListener("change", () => {
      state.voice.settings.aiCleanup = Boolean(el.voiceAiCleanupToggle.checked);
      persistState();
      renderRuntimeStatus();
    });

    el.voiceLanguage.addEventListener("change", () => {
      state.voice.settings.language = (el.voiceLanguage.value || "en-US").trim() || "en-US";
      persistState();
      renderRuntimeStatus();
    });

    document.addEventListener("keydown", (event) => {
      if (event.ctrlKey && event.key.toLowerCase() === "k") {
        event.preventDefault();
        openPalette();
        return;
      }

      if (event.ctrlKey && event.key.toLowerCase() === "m") {
        event.preventDefault();
        void toggleVoiceCapture();
        return;
      }

      if (event.key === "Escape" && state.paletteOpen) {
        event.preventDefault();
        closePalette();
      }
    });

    el.paletteBackdrop.addEventListener("click", closePalette);

    el.paletteInput.addEventListener("input", () => {
      state.paletteQuery = el.paletteInput.value || "";
      state.paletteIndex = 0;
      renderPalette();
    });

    el.paletteInput.addEventListener("keydown", (event) => {
      const commands = getPaletteCommands();
      if (event.key === "ArrowDown") {
        event.preventDefault();
        state.paletteIndex = Math.min(state.paletteIndex + 1, Math.max(commands.length - 1, 0));
        renderPalette();
        return;
      }

      if (event.key === "ArrowUp") {
        event.preventDefault();
        state.paletteIndex = Math.max(state.paletteIndex - 1, 0);
        renderPalette();
        return;
      }

      if (event.key === "Enter") {
        event.preventDefault();
        const command = commands[state.paletteIndex];
        if (command) {
          command.action();
          closePalette();
        }
      }
    });
  }

  function loadState() {
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY);
      if (!raw) {
        seedNotes();
        return;
      }

      const parsed = JSON.parse(raw);
      if (!parsed || !Array.isArray(parsed.notes) || parsed.notes.length === 0) {
        seedNotes();
        return;
      }

      state.notes = parsed.notes
        .map((note) => ({
          id: note.id || createId(),
          title: (note.title || "Untitled note").toString(),
          content: (note.content || "").toString(),
          updatedAt: note.updatedAt || new Date().toISOString()
        }))
        .slice(0, MAX_NOTES);

      state.activeId = parsed.activeId || state.notes[0].id;
      state.previewTheme = normalizeTheme(parsed.previewTheme || "studio");
      state.lastSource = (parsed.lastSource || "local").toString();

      if (parsed.ai && typeof parsed.ai === "object") {
        state.ai.selectedModel = (parsed.ai.selectedModel || state.ai.selectedModel).toString();
      }

      if (parsed.voice && typeof parsed.voice === "object") {
        const settings = parsed.voice.settings || {};
        state.voice.settings.language = (settings.language || "en-US").toString();
        state.voice.settings.rawMode = Boolean(settings.rawMode);
        state.voice.settings.aiCleanup = Boolean(settings.aiCleanup);

        if (Array.isArray(parsed.voice.sessions)) {
          state.voice.sessions = parsed.voice.sessions
            .map((session) => normalizeVoiceSession(session))
            .filter(Boolean)
            .slice(-MAX_VOICE_SESSIONS);
        }
      }
    } catch {
      seedNotes();
    }
  }

  function seedNotes() {
    state.notes = [
      {
        id: createId(),
        title: "Launch plan",
        content: "# JD.Writer launch\n\n## Today\n- Ship a stronger Studio Lite layout\n- Add optional WebLLM local inference\n- Support live transcription on cursor\n\n## Risks\n- Keep readability deterministic\n- Handle unsupported browser APIs safely\n",
        updatedAt: new Date().toISOString()
      },
      {
        id: createId(),
        title: "Idea dump",
        content: "# Idea dump\n\n- Local model selector in runtime strip\n- Voice review log with cleanup trace\n- Keep slash commands fast and deterministic\n",
        updatedAt: new Date(Date.now() - 1000 * 60 * 11).toISOString()
      }
    ];

    state.activeId = state.notes[0].id;
    state.previewTheme = "studio";
    state.lastSource = "local";
    persistState();
  }

  function persistState() {
    const payload = {
      notes: state.notes.slice(0, MAX_NOTES),
      activeId: state.activeId,
      previewTheme: state.previewTheme,
      lastSource: state.lastSource,
      ai: {
        selectedModel: state.ai.selectedModel
      },
      voice: {
        settings: {
          language: state.voice.settings.language,
          rawMode: state.voice.settings.rawMode,
          aiCleanup: state.voice.settings.aiCleanup
        },
        sessions: state.voice.sessions.slice(-MAX_VOICE_SESSIONS)
      }
    };

    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
  }

  function ensureActiveNote() {
    if (!state.notes.length) {
      seedNotes();
      return;
    }

    const exists = state.notes.some((note) => note.id === state.activeId);
    if (!exists) {
      state.activeId = state.notes[0].id;
    }
  }

  function createNote() {
    const note = {
      id: createId(),
      title: "Untitled note",
      content: "# New note\n\n",
      updatedAt: new Date().toISOString()
    };

    state.notes.unshift(note);
    state.notes = state.notes.slice(0, MAX_NOTES);
    state.activeId = note.id;
  }

  function getActiveNote() {
    return state.notes.find((note) => note.id === state.activeId) || null;
  }

  function renderAll() {
    renderHeader();
    renderNoteList();
    renderActiveNote();
    renderPreview();
    renderInsights();
    renderVoiceReview();
    renderRuntimeStatus();
    renderModelDiagnostics();
    renderPalette();
  }

  function renderHeader() {
    el.noteCount.textContent = String(state.notes.length);
    el.sourcePill.textContent = state.lastSource;
  }

  function renderNoteList() {
    const query = state.query.trim().toLowerCase();
    const filtered = state.notes.filter((note) => {
      if (!query) {
        return true;
      }

      return note.title.toLowerCase().includes(query) || note.content.toLowerCase().includes(query);
    });

    const html = filtered
      .map((note) => {
        const isActive = note.id === state.activeId ? " active" : "";
        const preview = escapeHtml(collapse(note.content, 72));
        const time = formatLocal(note.updatedAt);

        return `<button class="note-item${isActive}" data-note-id="${note.id}" type="button">
          <strong>${escapeHtml(note.title)}</strong>
          <span>${preview}</span>
          <time>${time}</time>
        </button>`;
      })
      .join("");

    el.noteList.innerHTML = html || `<p class="hint-text">No matching notes.</p>`;

    el.noteList.querySelectorAll("[data-note-id]").forEach((button) => {
      button.addEventListener("click", () => {
        state.activeId = button.getAttribute("data-note-id") || "";
        renderAll();
        persistState();
      });
    });
  }

  function renderActiveNote() {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    el.title.value = note.title;
    el.content.value = note.content;
    el.previewTheme.value = state.previewTheme;
    el.modelSelect.value = state.ai.selectedModel;
    el.voiceRawModeToggle.checked = state.voice.settings.rawMode;
    el.voiceAiCleanupToggle.checked = state.voice.settings.aiCleanup;
    el.voiceLanguage.value = state.voice.settings.language;
    updateAutocomplete();
    renderSlashSuggestions();
  }

  function renderPreview() {
    const note = getActiveNote();
    if (!note) {
      el.preview.innerHTML = "";
      return;
    }

    el.preview.className = `preview theme-${state.previewTheme}`;
    el.preview.innerHTML = markdownToHtml(note.content || "");
  }

  async function continueDraft() {
    if (state.busy.continue) {
      return;
    }

    const note = getActiveNote();
    if (!note) {
      return;
    }

    state.busy.continue = true;
    const originalText = el.continueButton.textContent;
    el.continueButton.disabled = true;
    el.continueButton.textContent = "Generating...";

    try {
      let generated = "";
      let source = "heuristic";

      if (state.ai.ready) {
        generated = await buildContinuationWithModel(note.content || "");
        if (generated) {
          source = `webllm:${state.ai.selectedModel}`;
        }
      }

      if (!generated) {
        generated = buildContinuation(note.content || "");
        source = "heuristic";
      }

      note.content = appendToDraft(note.content, generated);
      note.updatedAt = new Date().toISOString();
      state.lastSource = source;

      renderAll();
      persistState();
      await refreshInsights({ preferModel: true });
    } catch (error) {
      state.ai.error = error instanceof Error ? error.message : String(error);
      renderRuntimeStatus();
      renderModelDiagnostics();
    } finally {
      state.busy.continue = false;
      el.continueButton.disabled = false;
      el.continueButton.textContent = originalText || "AI Continue";
    }
  }

  function exportActiveNote() {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    const filename = slugify(note.title || "note") + ".md";
    const blob = new Blob([note.content || ""], { type: "text/markdown;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
  }

  function queueInsightsRefresh() {
    window.clearTimeout(insightsTimer);
    insightsTimer = window.setTimeout(() => {
      void refreshInsights({ preferModel: false });
    }, INSIGHT_DEBOUNCE_MS);
  }

  async function refreshInsights(options = {}) {
    const preferModel = Boolean(options.preferModel);
    const draft = getActiveNote()?.content || "";
    const requestId = ++state.ai.insightRequestId;

    if (state.busy.insights) {
      return;
    }

    state.busy.insights = true;
    const originalText = el.insightsButton.textContent;
    el.insightsButton.disabled = true;
    el.insightsButton.textContent = preferModel ? "Thinking..." : "Refreshing...";

    try {
      let nextInsights = null;

      if (preferModel && state.ai.ready) {
        nextInsights = await buildModelInsights(draft);
      }

      if (!nextInsights) {
        nextInsights = {
          hints: buildAssistLines("hints", draft),
          help: buildAssistLines("help", draft),
          brainstorm: buildAssistLines("brainstorm", draft)
        };
      }

      if (requestId !== state.ai.insightRequestId) {
        return;
      }

      state.insights.hints = nextInsights.hints;
      state.insights.help = nextInsights.help;
      state.insights.brainstorm = nextInsights.brainstorm;
      state.lastSource = nextInsights.source || (preferModel && state.ai.ready ? "webllm" : "local");
      renderInsights();
      renderHeader();
      persistState();
    } catch (error) {
      state.ai.error = error instanceof Error ? error.message : String(error);
      state.insights.hints = buildAssistLines("hints", draft);
      state.insights.help = buildAssistLines("help", draft);
      state.insights.brainstorm = buildAssistLines("brainstorm", draft);
      renderInsights();
      renderModelDiagnostics();
    } finally {
      state.busy.insights = false;
      el.insightsButton.disabled = false;
      el.insightsButton.textContent = originalText || "Refresh";
    }
  }

  function renderInsights() {
    renderInsightList(el.hintsList, state.insights.hints);
    renderInsightList(el.helpList, state.insights.help);
    renderInsightList(el.brainstormList, state.insights.brainstorm);
  }

  function renderInsightList(target, items) {
    target.innerHTML = items.map((item) => `<li>${escapeHtml(item)}</li>`).join("");
  }

  function renderVoiceReview() {
    if (!state.voice.sessions.length) {
      el.voiceReviewList.innerHTML = `<li class="hint-text">No voice sessions captured yet.</li>`;
      return;
    }

    const sessions = state.voice.sessions.slice(-4).reverse();
    const html = sessions
      .map((session) => {
        const status = escapeHtml(session.status || "ready");
        const time = escapeHtml(formatLocal(session.startedAt));
        const tailEvents = (session.events || []).slice(-3);
        const eventSummary = tailEvents
          .map((evt) => `${evt.kind}: ${collapse(evt.text, 54)}`)
          .join(" | ");
        return `<li><strong>${time}</strong> <span>(${status})</span><br>${escapeHtml(eventSummary || "No transcript events yet.")}</li>`;
      })
      .join("");

    el.voiceReviewList.innerHTML = html;
  }

  function updateAutocomplete() {
    const note = getActiveNote();
    if (!note) {
      state.autocompleteSuffix = "";
      state.activeWord = "";
      renderAutocomplete();
      return;
    }

    const input = el.content;
    const beforeCaret = input.value.slice(0, input.selectionStart);
    const match = /([A-Za-z][A-Za-z\-]{1,})$/.exec(beforeCaret);

    if (!match) {
      state.autocompleteSuffix = "";
      state.activeWord = "";
      renderAutocomplete();
      return;
    }

    const prefix = match[1];
    const corpus = state.notes.map((n) => n.content || "").join(" ");
    const words = (corpus.match(/\b[A-Za-z][A-Za-z\-]{2,}\b/g) || [])
      .filter((token) => token.toLowerCase().startsWith(prefix.toLowerCase()) && token.toLowerCase() !== prefix.toLowerCase());

    const frequency = new Map();
    for (const word of words) {
      const key = word.toLowerCase();
      frequency.set(key, (frequency.get(key) || 0) + 1);
    }

    let candidate = "";
    let bestScore = -1;
    for (const [key, count] of frequency.entries()) {
      if (count > bestScore) {
        bestScore = count;
        candidate = key;
      }
    }

    if (!candidate || candidate.length <= prefix.length) {
      state.autocompleteSuffix = "";
      state.activeWord = "";
      renderAutocomplete();
      return;
    }

    state.activeWord = prefix;
    state.autocompleteSuffix = candidate.slice(prefix.length);
    renderAutocomplete();
  }

  function renderAutocomplete() {
    if (!state.autocompleteSuffix) {
      el.autocompleteChip.hidden = true;
      el.autocompleteChip.textContent = "";
      return;
    }

    el.autocompleteChip.hidden = false;
    el.autocompleteChip.textContent = `Autocomplete: ${state.activeWord}${state.autocompleteSuffix}`;
  }

  function acceptAutocomplete() {
    if (!state.autocompleteSuffix) {
      return;
    }

    insertTextAtCursor(el.content, state.autocompleteSuffix);
    syncActiveNoteFromEditor("local");
  }

  function renderSlashSuggestions() {
    const context = getSlashContext();
    state.slashContext = context;

    if (!context) {
      el.slashBar.hidden = true;
      el.slashBar.innerHTML = "";
      return;
    }

    const matches = slashCommands.filter((command) => command.name.startsWith(context.token));
    if (!matches.length) {
      el.slashBar.hidden = true;
      el.slashBar.innerHTML = "";
      return;
    }

    el.slashBar.hidden = false;
    el.slashBar.innerHTML = matches
      .map((command) => `<button class="slash-item" data-slash="${command.name}" type="button">/${command.name}</button>`)
      .join("");

    el.slashBar.querySelectorAll("[data-slash]").forEach((button) => {
      button.addEventListener("click", () => {
        const name = button.getAttribute("data-slash") || "";
        const command = slashCommands.find((item) => item.name === name);
        if (command) {
          void applySlashCommand(command);
        }
      });
    });
  }

  function getSlashContext() {
    const cursor = el.content.selectionStart;
    const before = el.content.value.slice(0, cursor);
    const match = /(?:^|\s)\/([a-z-]*)$/.exec(before);
    if (!match) {
      return null;
    }

    const token = (match[1] || "").trim().toLowerCase();
    const slashToken = "/" + token;
    return {
      token,
      start: cursor - slashToken.length,
      length: slashToken.length
    };
  }

  async function applySlashCommand(command) {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    const context = state.slashContext;
    if (context) {
      el.content.value = replaceRange(el.content.value, context.start, context.start + context.length, "").replace(/\s+$/, "");
    }

    let transformed = "";
    let source = "heuristic";

    if (state.ai.ready) {
      transformed = await runSlashCommandWithModel(command.name, el.content.value || "");
      if (transformed) {
        source = `webllm:${state.ai.selectedModel}`;
      }
    }

    if (!transformed) {
      transformed = command.run(el.content.value || "");
      source = "heuristic";
    }

    el.content.value = appendToDraft(el.content.value, transformed.trim());
    note.content = el.content.value;
    note.updatedAt = new Date().toISOString();
    state.lastSource = source;

    renderAll();
    persistState();
    await refreshInsights({ preferModel: state.ai.ready });
  }

  function openPalette() {
    state.paletteOpen = true;
    state.paletteQuery = "";
    state.paletteIndex = 0;
    el.palette.hidden = false;
    el.paletteBackdrop.hidden = false;
    el.paletteInput.value = "";
    renderPalette();
    window.setTimeout(() => el.paletteInput.focus(), 0);
  }

  function closePalette() {
    state.paletteOpen = false;
    el.palette.hidden = true;
    el.paletteBackdrop.hidden = true;
  }

  function getPaletteCommands() {
    const commands = [
      {
        title: "Create note",
        description: "Create a new markdown note.",
        action: () => {
          createNote();
          renderAll();
          persistState();
          void refreshInsights({ preferModel: false });
        }
      },
      {
        title: "AI continue",
        description: "Append continuation using loaded model or fallback heuristics.",
        action: () => {
          void continueDraft();
        }
      },
      {
        title: "Refresh insights",
        description: "Rebuild hints/help/brainstorm for current draft.",
        action: () => {
          void refreshInsights({ preferModel: true });
        }
      },
      {
        title: state.voice.active ? "Stop voice capture" : "Start voice capture",
        description: "Toggle browser speech transcription at cursor.",
        action: () => {
          void toggleVoiceCapture();
        }
      },
      {
        title: state.ai.ready ? "Unload local model" : "Load local model",
        description: "Toggle WebLLM runtime for local model generation.",
        action: () => {
          void toggleModelRuntime();
        }
      },
      {
        title: "Export markdown",
        description: "Download the active note as .md.",
        action: exportActiveNote
      },
      ...slashCommands.map((cmd) => ({
        title: `Run /${cmd.name}`,
        description: cmd.description,
        action: () => {
          void applySlashCommand(cmd);
        }
      }))
    ];

    const query = state.paletteQuery.trim().toLowerCase();
    if (!query) {
      return commands;
    }

    return commands.filter((command) =>
      command.title.toLowerCase().includes(query) || command.description.toLowerCase().includes(query));
  }

  function renderPalette() {
    if (!state.paletteOpen) {
      return;
    }

    const commands = getPaletteCommands();
    if (state.paletteIndex >= commands.length) {
      state.paletteIndex = Math.max(commands.length - 1, 0);
    }

    el.paletteList.innerHTML = commands
      .map((command, index) => {
        const activeClass = index === state.paletteIndex ? " active" : "";
        return `<button class="palette-item${activeClass}" data-index="${index}" type="button">
          <strong>${escapeHtml(command.title)}</strong>
          <span>${escapeHtml(command.description)}</span>
        </button>`;
      })
      .join("");

    el.paletteList.querySelectorAll("[data-index]").forEach((button) => {
      button.addEventListener("click", () => {
        const index = Number(button.getAttribute("data-index"));
        const command = commands[index];
        if (command) {
          command.action();
          closePalette();
        }
      });
    });
  }

  function detectVoiceSupport() {
    const Ctor = window.SpeechRecognition || window.webkitSpeechRecognition;
    state.voice.supported = typeof Ctor === "function";
  }

  async function toggleVoiceCapture() {
    if (!state.voice.supported) {
      setRuntimeMessage("Speech recognition is not supported in this browser.");
      return;
    }

    if (state.voice.active) {
      stopVoiceCapture();
      return;
    }

    startVoiceCapture();
  }

  function startVoiceCapture() {
    const Ctor = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (typeof Ctor !== "function") {
      state.voice.supported = false;
      renderRuntimeStatus();
      return;
    }

    stopVoiceCapture();

    const recognition = new Ctor();
    recognition.lang = state.voice.settings.language || "en-US";
    recognition.interimResults = true;
    recognition.continuous = true;

    state.voice.recognition = recognition;
    state.voice.active = true;
    state.voice.interimRange = null;
    state.voice.activeSession = {
      id: createId(),
      startedAt: new Date().toISOString(),
      endedAt: "",
      status: "recording",
      events: []
    };
    pushVoiceEvent("session-started", `Language: ${recognition.lang}`);
    renderRuntimeStatus();
    renderVoiceReview();

    recognition.onresult = (event) => {
      let interimText = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        const text = (result[0]?.transcript || "").trim();
        if (!text) {
          continue;
        }

        if (result.isFinal) {
          finalizeVoiceText(text);
        } else {
          interimText = text;
        }
      }

      if (interimText) {
        applyInterimVoiceText(interimText);
      }
    };

    recognition.onerror = (event) => {
      const error = event?.error || "unknown-error";
      pushVoiceEvent("speech-error", error);
      state.voice.active = false;
      state.voice.interimRange = null;
      setRuntimeMessage(`Voice capture error: ${error}`);
      finalizeVoiceSession("error");
      renderRuntimeStatus();
      renderVoiceReview();
    };

    recognition.onend = () => {
      if (state.voice.active) {
        state.voice.active = false;
        state.voice.interimRange = null;
        finalizeVoiceSession("stopped");
        renderRuntimeStatus();
        renderVoiceReview();
      }
    };

    try {
      recognition.start();
      setRuntimeMessage("Voice capture started. Speech text will stream at cursor.");
    } catch (error) {
      state.voice.active = false;
      pushVoiceEvent("speech-error", error instanceof Error ? error.message : String(error));
      finalizeVoiceSession("error");
      renderRuntimeStatus();
      renderVoiceReview();
      setRuntimeMessage("Unable to start speech recognition.");
    }
  }

  function stopVoiceCapture() {
    if (!state.voice.recognition) {
      state.voice.active = false;
      return;
    }

    try {
      state.voice.recognition.stop();
    } catch {
      // best effort
    }

    state.voice.active = false;
    state.voice.interimRange = null;
    finalizeVoiceSession("stopped");
    renderRuntimeStatus();
    renderVoiceReview();
    setRuntimeMessage("Voice capture stopped.");
  }

  function applyInterimVoiceText(text) {
    const cleaned = normalizeVoiceText(text);
    if (!cleaned) {
      return;
    }

    const range = insertVoiceText(cleaned, { interim: true });
    state.voice.interimRange = range;
    pushVoiceEvent("transcript-interim", cleaned);
    state.lastSource = "speech-interim";
    renderHeader();
    renderVoiceReview();
  }

  function finalizeVoiceText(text) {
    const cleaned = normalizeVoiceText(text);
    if (!cleaned) {
      return;
    }

    const finalRange = insertVoiceText(cleaned, { interim: false });
    state.voice.interimRange = null;
    pushVoiceEvent("transcript-finalized", cleaned);
    state.lastSource = "speech";
    renderHeader();
    renderVoiceReview();

    if (!state.voice.settings.rawMode && state.voice.settings.aiCleanup) {
      void cleanupVoiceRange(finalRange, cleaned);
    }
  }

  function insertVoiceText(text, options) {
    const useInterimRange = options && options.interim && state.voice.interimRange;
    let start = 0;
    let end = 0;

    if (useInterimRange) {
      start = state.voice.interimRange.start;
      end = state.voice.interimRange.end;
    } else if (state.voice.interimRange) {
      start = state.voice.interimRange.start;
      end = state.voice.interimRange.end;
    } else {
      start = typeof el.content.selectionStart === "number" ? el.content.selectionStart : el.content.value.length;
      end = typeof el.content.selectionEnd === "number" ? el.content.selectionEnd : start;
    }

    const normalized = withSpacing(el.content.value, start, end, text);
    el.content.value = replaceRange(el.content.value, start, end, normalized.value);
    const range = {
      start,
      end: start + normalized.value.length
    };

    el.content.focus();
    el.content.setSelectionRange(range.end, range.end);
    syncActiveNoteFromEditor(options && options.interim ? "speech-interim" : "speech");
    return range;
  }

  async function cleanupVoiceRange(range, rawText) {
    try {
      const polished = await buildVoiceCleanup(rawText);
      if (!polished || polished === rawText) {
        pushVoiceEvent("voice-cleanup-skipped", rawText);
        return;
      }

      const currentSegment = el.content.value.slice(range.start, range.end);
      if (!currentSegment) {
        return;
      }

      el.content.value = replaceRange(el.content.value, range.start, range.end, polished);
      el.content.setSelectionRange(range.start + polished.length, range.start + polished.length);
      syncActiveNoteFromEditor(state.ai.ready ? "voice-cleanup-webllm" : "voice-cleanup-local");
      pushVoiceEvent("voice-cleanup-applied", polished);
      renderVoiceReview();
    } catch (error) {
      pushVoiceEvent("voice-cleanup-error", error instanceof Error ? error.message : String(error));
      renderVoiceReview();
    }
  }

  function finalizeVoiceSession(status) {
    const activeSession = state.voice.activeSession;
    if (!activeSession) {
      return;
    }

    activeSession.status = status || "stopped";
    activeSession.endedAt = new Date().toISOString();
    state.voice.sessions.push(activeSession);
    state.voice.sessions = state.voice.sessions.slice(-MAX_VOICE_SESSIONS);
    state.voice.activeSession = null;
    persistState();
  }

  function pushVoiceEvent(kind, text) {
    if (!state.voice.activeSession) {
      return;
    }

    state.voice.activeSession.events.push({
      kind: (kind || "event").toString(),
      text: collapse((text || "").toString(), 480),
      at: new Date().toISOString()
    });
    state.voice.activeSession.events = state.voice.activeSession.events.slice(-MAX_VOICE_EVENTS);
  }

  function renderRuntimeStatus() {
    el.modelPill.textContent = state.ai.ready ? "webllm" : "heuristic";
    if (state.voice.active) {
      el.voicePill.textContent = "recording";
    } else if (state.voice.supported) {
      el.voicePill.textContent = "ready";
    } else {
      el.voicePill.textContent = "unsupported";
    }

    if (state.ai.loading) {
      el.modelLoadButton.textContent = "Loading...";
      el.modelLoadButton.disabled = true;
    } else if (state.ai.ready) {
      el.modelLoadButton.textContent = "Unload Model";
      el.modelLoadButton.disabled = false;
    } else {
      el.modelLoadButton.textContent = "Load Model";
      el.modelLoadButton.disabled = false;
    }

    el.voiceButton.textContent = state.voice.active ? "Mic (On)" : "Mic (Off)";

    if (state.ai.error) {
      setRuntimeMessage(`Model runtime error: ${state.ai.error}`);
    } else if (state.ai.loading) {
      const progress = state.ai.progress ? ` ${state.ai.progress}` : "";
      setRuntimeMessage(`Loading ${state.ai.selectedModel}...${progress}`.trim());
    } else if (state.ai.ready) {
      setRuntimeMessage(`Model ready: ${state.ai.selectedModel}`);
    } else {
      setRuntimeMessage("Running with deterministic local heuristics.");
    }
  }

  function setRuntimeMessage(message) {
    if (el.runtimeStatus) {
      el.runtimeStatus.textContent = message;
    }
  }

  function renderModelDiagnostics() {
    const diagnostics = [
      `Engine ready: ${state.ai.ready ? "yes" : "no"}`,
      `Selected model: ${state.ai.selectedModel}`,
      `WebGPU: ${navigator.gpu ? "available" : "unavailable"}`,
      `SpeechRecognition: ${state.voice.supported ? "available" : "unavailable"}`,
      `Voice mode: ${state.voice.settings.rawMode ? "raw" : "assisted"}`
    ];

    if (state.ai.progress) {
      diagnostics.push(`Load progress: ${state.ai.progress}`);
    }
    if (state.ai.error) {
      diagnostics.push(`Last error: ${state.ai.error}`);
    }

    el.modelStatusDetail.textContent = state.ai.ready
      ? `Loaded ${state.ai.selectedModel}. Inference runs in this browser tab.`
      : "Model runtime is not loaded.";
    el.modelDiagnostics.innerHTML = diagnostics.map((line) => `<li>${escapeHtml(line)}</li>`).join("");
  }

  async function toggleModelRuntime() {
    if (state.ai.loading) {
      return;
    }

    if (state.ai.ready) {
      await unloadModelRuntime();
      return;
    }

    await loadModelRuntime();
  }

  async function loadModelRuntime() {
    state.ai.loading = true;
    state.ai.error = "";
    state.ai.progress = "";
    renderRuntimeStatus();
    renderModelDiagnostics();

    try {
      const module = await importWebLLMModule();
      state.ai.module = module;

      const engine = await createModelEngine(module, state.ai.selectedModel, (progress) => {
        state.ai.progress = progress;
        renderRuntimeStatus();
        renderModelDiagnostics();
      });

      state.ai.engine = engine;
      state.ai.ready = true;
      state.ai.loading = false;
      state.ai.progress = "ready";
      state.lastSource = `webllm:${state.ai.selectedModel}`;
      renderHeader();
      renderRuntimeStatus();
      renderModelDiagnostics();
      persistState();
      await refreshInsights({ preferModel: true });
    } catch (error) {
      state.ai.ready = false;
      state.ai.loading = false;
      state.ai.engine = null;
      state.ai.progress = "";
      state.ai.error = error instanceof Error ? error.message : String(error);
      renderRuntimeStatus();
      renderModelDiagnostics();
    }
  }

  async function unloadModelRuntime() {
    state.ai.loading = true;
    renderRuntimeStatus();
    renderModelDiagnostics();

    try {
      const engine = state.ai.engine;
      if (engine && typeof engine.unload === "function") {
        await engine.unload();
      }
      if (engine && typeof engine.dispose === "function") {
        await maybePromise(engine.dispose());
      }
    } catch (error) {
      state.ai.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.ai.engine = null;
      state.ai.module = null;
      state.ai.ready = false;
      state.ai.loading = false;
      state.ai.progress = "";
      renderRuntimeStatus();
      renderModelDiagnostics();
    }
  }

  async function importWebLLMModule() {
    let lastError = null;
    for (const candidate of WEBLLM_IMPORT_CANDIDATES) {
      try {
        const module = await import(candidate);
        if (module) {
          return module;
        }
      } catch (error) {
        lastError = error;
      }
    }

    throw new Error(lastError instanceof Error ? lastError.message : "Unable to import @mlc-ai/web-llm");
  }

  async function createModelEngine(module, modelId, onProgress) {
    const progressCallback = (report) => {
      onProgress(formatModelProgress(report));
    };

    if (typeof module.CreateMLCEngine === "function") {
      return module.CreateMLCEngine(modelId, { initProgressCallback: progressCallback });
    }

    if (typeof module.createMLCEngine === "function") {
      return module.createMLCEngine(modelId, { initProgressCallback: progressCallback });
    }

    if (typeof module.MLCEngine === "function") {
      const engine = new module.MLCEngine({ initProgressCallback: progressCallback });
      if (typeof engine.reload === "function") {
        await engine.reload(modelId);
      }
      return engine;
    }

    throw new Error("WebLLM module API is unsupported in this browser.");
  }

  async function runModelPrompt(systemPrompt, userPrompt, options = {}) {
    if (!state.ai.ready || !state.ai.engine) {
      return "";
    }

    const maxTokens = Number(options.maxTokens || 280);
    const temperature = Number(options.temperature || 0.35);
    const engine = state.ai.engine;

    if (engine.chat && engine.chat.completions && typeof engine.chat.completions.create === "function") {
      const result = await engine.chat.completions.create({
        stream: false,
        temperature,
        max_tokens: maxTokens,
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userPrompt }
        ]
      });
      return completionToText(result);
    }

    if (typeof engine.generate === "function") {
      const prompt = `${systemPrompt}\n\n${userPrompt}`;
      const generated = await engine.generate(prompt, { maxTokens, temperature });
      return typeof generated === "string" ? generated : String(generated || "");
    }

    return "";
  }

  async function completionToText(result) {
    if (!result) {
      return "";
    }

    if (typeof result[Symbol.asyncIterator] === "function") {
      let streamed = "";
      for await (const chunk of result) {
        const delta = chunk?.choices?.[0]?.delta?.content;
        if (typeof delta === "string") {
          streamed += delta;
        } else if (Array.isArray(delta)) {
          streamed += delta
            .map((item) => (typeof item === "string" ? item : item?.text || ""))
            .join("");
        }
      }
      return streamed.trim();
    }

    const choice = result?.choices?.[0];
    if (!choice) {
      return "";
    }

    const content = choice?.message?.content ?? choice?.delta?.content ?? "";
    if (Array.isArray(content)) {
      return content
        .map((item) => (typeof item === "string" ? item : item?.text || ""))
        .join("")
        .trim();
    }

    return String(content || "").trim();
  }

  async function buildContinuationWithModel(draft) {
    const promptWindow = trimForPrompt(draft, 6200);
    const systemPrompt = "You are JD.Writer. Continue markdown naturally in the same tone. Return only markdown to append.";
    const userPrompt = `Continue this draft:\n\n${promptWindow}`;
    const text = await runModelPrompt(systemPrompt, userPrompt, {
      maxTokens: 260,
      temperature: 0.3
    });
    return text.trim();
  }

  async function runSlashCommandWithModel(command, draft) {
    const normalized = (command || "").trim().toLowerCase();
    const promptWindow = trimForPrompt(draft, 6200);

    const directive = normalized === "summarize"
      ? "Summarize this markdown draft into concise bullets."
      : normalized === "outline"
        ? "Convert this draft into a clean markdown outline with sections."
        : normalized === "action-items"
          ? "Extract concrete markdown checkbox action items."
          : `Apply slash command '${normalized}' to improve this draft.`;

    const output = await runModelPrompt(
      "You are JD.Writer slash command runtime. Return markdown only.",
      `${directive}\n\nDraft:\n${promptWindow}`,
      { maxTokens: 300, temperature: 0.25 }
    );

    return output.trim();
  }

  async function buildModelInsights(draft) {
    const promptWindow = trimForPrompt(draft, 3800);
    const systemPrompt = "Return strict JSON only. No markdown fences.";
    const userPrompt = `Given this markdown draft, return JSON with keys \"hints\", \"help\", \"brainstorm\". Each value is an array of 5 concise strings, each under 12 words.\n\nDraft:\n${promptWindow}`;
    const response = await runModelPrompt(systemPrompt, userPrompt, {
      maxTokens: 360,
      temperature: 0.3
    });

    const parsed = tryParseInsightsJson(response);
    if (!parsed) {
      return null;
    }

    return {
      hints: parsed.hints,
      help: parsed.help,
      brainstorm: parsed.brainstorm,
      source: `webllm:${state.ai.selectedModel}`
    };
  }

  function tryParseInsightsJson(raw) {
    if (!raw) {
      return null;
    }

    const text = raw.trim();
    const start = text.indexOf("{");
    const end = text.lastIndexOf("}");
    if (start < 0 || end <= start) {
      return null;
    }

    const candidate = text.slice(start, end + 1);
    try {
      const parsed = JSON.parse(candidate);
      const hints = normalizeInsightArray(parsed.hints);
      const help = normalizeInsightArray(parsed.help);
      const brainstorm = normalizeInsightArray(parsed.brainstorm);
      if (!hints.length || !help.length || !brainstorm.length) {
        return null;
      }

      return { hints, help, brainstorm };
    } catch {
      return null;
    }
  }

  function normalizeInsightArray(value) {
    if (!Array.isArray(value)) {
      return [];
    }

    return value
      .map((item) => (item == null ? "" : String(item).trim()))
      .filter(Boolean)
      .slice(0, 8);
  }

  async function buildVoiceCleanup(text) {
    const source = normalizeVoiceText(text);
    if (!source) {
      return "";
    }

    if (state.ai.ready) {
      const prompt = await runModelPrompt(
        "Clean voice transcription text. Preserve meaning. Keep markdown-safe prose. Return plain text only.",
        source,
        { maxTokens: 120, temperature: 0.18 }
      );
      const cleaned = normalizeVoiceText(prompt);
      if (cleaned) {
        return cleaned;
      }
    }

    const compact = source.replace(/\s+/g, " ").trim();
    if (!compact) {
      return "";
    }

    const sentence = compact.charAt(0).toUpperCase() + compact.slice(1);
    return /[.!?]$/.test(sentence) ? sentence : `${sentence}.`;
  }

  function buildContinuation(draft) {
    const fallbackLine = (draft || "")
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean)
      .pop() || "the current draft";

    return `## Next Move\n\nBuild on \"${fallbackLine}\" by converting it into 3 concrete actions:\n\n- Define the smallest usable workflow and test it end-to-end.\n- Capture one measurable success metric for this pass.\n- List one risk and one mitigation before implementation.`;
  }

  function summarizeDraft(draft) {
    const lines = (draft || "")
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean)
      .slice(0, 6);

    const bullets = lines.map((line) => `- ${line.replace(/^[-#\s]+/, "")}`);
    return `## Summary\n\n${bullets.join("\n") || "- Core idea captured."}`;
  }

  function outlineDraft(draft) {
    const headings = (draft.match(/^#{1,3}\s+.+$/gm) || [])
      .map((line) => line.replace(/^#+\s*/, "").trim())
      .slice(0, 4);

    const sections = headings.length ? headings : ["Context", "Approach", "Next Actions"];
    return `## Outline\n\n${sections.map((section) => `### ${section}\n- `).join("\n\n")}`;
  }

  function actionItemsDraft(draft) {
    const text = (draft || "").replace(/\s+/g, " ").trim();
    const chunks = text.split(/[.!?]/).map((item) => item.trim()).filter(Boolean).slice(0, 4);
    const items = chunks.length
      ? chunks.map((chunk) => `- [ ] ${chunk}`)
      : ["- [ ] Define owner for each task.", "- [ ] Add due dates.", "- [ ] Track one risk per task."];

    return `## Action Items\n\n${items.join("\n")}`;
  }

  function buildAssistLines(mode, draft) {
    const lineCount = (draft.match(/\n/g) || []).length + 1;
    const wordCount = (draft.match(/[A-Za-z']+/g) || []).length;
    const hasHeading = /(^|\n)#\s+/.test(draft || "");

    if (mode === "help") {
      return [
        "Use #, ##, ### headings to keep sections scannable.",
        "Fence commands and code snippets in triple backticks.",
        "Keep paragraphs under four lines for readability.",
        "Use checkboxes for concrete execution steps.",
        "Ctrl+J appends continuation; Ctrl+K opens commands."
      ];
    }

    if (mode === "brainstorm") {
      return [
        "Draft one plugin that rewrites tone automatically.",
        "Create a meeting-note template with decision blocks.",
        "Map markdown capture flow from idea to publish.",
        "Define one quality gate for each note type.",
        "Try two preview themes and compare readability."
      ];
    }

    return [
      hasHeading ? "Heading structure looks solid; add one closing summary." : "Start with one explicit heading for stronger scanability.",
      wordCount < 100 ? "Add one concrete example to ground the concept." : "Trim repeated phrasing to improve pace.",
      lineCount < 8 ? "Add a short checklist for next actions." : "Group related lines under subheadings.",
      "Mark unknowns with TODO tags for follow-up.",
      "Close with one measurable success metric."
    ];
  }

  function markdownToHtml(markdown) {
    const source = (markdown || "").replace(/\r/g, "");
    const lines = source.split("\n");
    const html = [];

    let inCode = false;
    let codeBuffer = [];
    let inList = false;
    let paragraphBuffer = [];

    function flushParagraph() {
      if (!paragraphBuffer.length) {
        return;
      }

      html.push(`<p>${inline(paragraphBuffer.join(" "))}</p>`);
      paragraphBuffer = [];
    }

    function flushList() {
      if (!inList) {
        return;
      }

      html.push("</ul>");
      inList = false;
    }

    for (const rawLine of lines) {
      const line = rawLine || "";

      if (line.trim().startsWith("```")) {
        flushParagraph();
        flushList();
        if (inCode) {
          html.push(`<pre><code>${escapeHtml(codeBuffer.join("\n"))}</code></pre>`);
          codeBuffer = [];
          inCode = false;
        } else {
          inCode = true;
        }
        continue;
      }

      if (inCode) {
        codeBuffer.push(line);
        continue;
      }

      if (!line.trim()) {
        flushParagraph();
        flushList();
        continue;
      }

      const heading = /^(#{1,4})\s+(.+)$/.exec(line);
      if (heading) {
        flushParagraph();
        flushList();
        const level = heading[1].length;
        html.push(`<h${level}>${inline(heading[2].trim())}</h${level}>`);
        continue;
      }

      const list = /^-\s+(.+)$/.exec(line);
      if (list) {
        flushParagraph();
        if (!inList) {
          html.push("<ul>");
          inList = true;
        }
        html.push(`<li>${inline(list[1].trim())}</li>`);
        continue;
      }

      paragraphBuffer.push(line.trim());
    }

    if (inCode) {
      html.push(`<pre><code>${escapeHtml(codeBuffer.join("\n"))}</code></pre>`);
    }

    flushParagraph();
    flushList();

    return html.join("\n");
  }

  function inline(text) {
    let value = escapeHtml(text || "");
    value = value.replace(/`([^`]+)`/g, "<code>$1</code>");
    value = value.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    value = value.replace(/\*([^*]+)\*/g, "<em>$1</em>");
    value = value.replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
    return value;
  }

  function syncActiveNoteFromEditor(source) {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    note.content = el.content.value;
    note.updatedAt = new Date().toISOString();
    state.lastSource = source || state.lastSource;
    renderPreview();
    renderNoteList();
    renderHeader();
    persistState();
  }

  function appendToDraft(base, addition) {
    const left = (base || "").trimEnd();
    const right = (addition || "").trim();
    if (!left) {
      return right;
    }

    return `${left}\n\n${right}`;
  }

  function replaceRange(value, start, end, replacement) {
    const safeStart = Math.max(0, Math.min(start, value.length));
    const safeEnd = Math.max(safeStart, Math.min(end, value.length));
    return value.slice(0, safeStart) + replacement + value.slice(safeEnd);
  }

  function insertTextAtCursor(target, text) {
    const start = typeof target.selectionStart === "number" ? target.selectionStart : target.value.length;
    const end = typeof target.selectionEnd === "number" ? target.selectionEnd : target.value.length;
    target.value = replaceRange(target.value, start, end, text);
    const cursor = start + text.length;
    target.setSelectionRange(cursor, cursor);
    target.dispatchEvent(new Event("input", { bubbles: true }));
  }

  function withSpacing(content, start, end, text) {
    let value = text;
    const before = content.slice(0, start);
    const after = content.slice(end);
    const needsLeadingSpace = before.length > 0 && !/\s$/.test(before);
    const needsTrailingSpace = after.length > 0 && !/^\s/.test(after);

    if (needsLeadingSpace) {
      value = " " + value;
    }
    if (needsTrailingSpace) {
      value = value + " ";
    }

    return { value };
  }

  function normalizeVoiceText(text) {
    return (text || "").replace(/\s+/g, " ").trim();
  }

  function normalizeVoiceSession(session) {
    if (!session || typeof session !== "object") {
      return null;
    }

    const events = Array.isArray(session.events)
      ? session.events
          .map((evt) => ({
            kind: (evt?.kind || "event").toString(),
            text: (evt?.text || "").toString(),
            at: evt?.at || new Date().toISOString()
          }))
          .slice(-MAX_VOICE_EVENTS)
      : [];

    return {
      id: session.id || createId(),
      startedAt: session.startedAt || new Date().toISOString(),
      endedAt: session.endedAt || "",
      status: (session.status || "stopped").toString(),
      events
    };
  }

  function formatModelProgress(report) {
    if (!report) {
      return "";
    }

    if (typeof report === "string") {
      return report;
    }

    if (typeof report === "number") {
      return `${Math.round(report * 100)}%`;
    }

    if (typeof report === "object") {
      const text = report.text || report.message || "";
      const progress = Number(report.progress);
      if (Number.isFinite(progress)) {
        const pct = Math.round(progress * 100);
        return text ? `${text} (${pct}%)` : `${pct}%`;
      }
      if (text) {
        return String(text);
      }
    }

    return "";
  }

  function maybePromise(value) {
    if (value && typeof value.then === "function") {
      return value;
    }
    return Promise.resolve(value);
  }

  function collapse(value, maxLength) {
    const compact = (value || "").replace(/\s+/g, " ").trim();
    if (compact.length <= maxLength) {
      return compact || "Empty";
    }

    return `${compact.slice(0, maxLength)}...`;
  }

  function formatLocal(iso) {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return "Unknown";
    }

    return date.toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit"
    });
  }

  function slugify(value) {
    return (value || "note")
      .toLowerCase()
      .replace(/[^a-z0-9\s-]/g, "")
      .trim()
      .replace(/\s+/g, "-") || "note";
  }

  function normalizeTheme(value) {
    return ["studio", "paper", "solarized", "terminal", "noir", "blueprint"].includes(value)
      ? value
      : "studio";
  }

  function trimForPrompt(value, maxLength) {
    if (!value) {
      return "";
    }
    return value.length <= maxLength ? value : value.slice(value.length - maxLength);
  }

  function createId() {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }

    return `id-${Math.random().toString(16).slice(2)}-${Date.now().toString(16)}`;
  }

  function escapeHtml(value) {
    return (value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/\"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }
})();

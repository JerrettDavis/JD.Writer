(() => {
  "use strict";

  const STORAGE_KEY = "jdwriter.pages.studio.v1";
  const INSIGHT_DEBOUNCE_MS = 800;

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
    }
  };

  const el = {
    noteCount: document.getElementById("note-count"),
    sourcePill: document.getElementById("source-pill"),
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
    hintsList: document.getElementById("hints-list"),
    helpList: document.getElementById("help-list"),
    brainstormList: document.getElementById("brainstorm-list"),
    autocompleteChip: document.getElementById("autocomplete-chip"),
    slashBar: document.getElementById("slash-bar"),
    paletteBackdrop: document.getElementById("palette-backdrop"),
    palette: document.getElementById("palette"),
    paletteInput: document.getElementById("palette-input"),
    paletteList: document.getElementById("palette-list")
  };

  const slashCommands = [
    {
      name: "summarize",
      description: "Condense the draft into key bullets.",
      run: (draft) => summarizeDraft(draft)
    },
    {
      name: "outline",
      description: "Generate a sectioned markdown outline.",
      run: (draft) => outlineDraft(draft)
    },
    {
      name: "action-items",
      description: "Extract concrete checkbox tasks.",
      run: (draft) => actionItemsDraft(draft)
    }
  ];

  let insightsTimer = 0;

  init();

  function init() {
    wireEvents();
    loadState();
    ensureActiveNote();
    renderAll();
    refreshInsights();
  }

  function wireEvents() {
    el.search.addEventListener("input", () => {
      state.query = el.search.value || "";
      renderNoteList();
    });

    el.newNote.addEventListener("click", () => {
      createNote();
      renderAll();
      persistState();
      refreshInsights();
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

    el.content.addEventListener("keydown", (event) => {
      if (event.ctrlKey && event.key.toLowerCase() === "j") {
        event.preventDefault();
        continueDraft();
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
          applySlashCommand(first);
        }
      }
    });

    el.continueButton.addEventListener("click", continueDraft);
    el.exportButton.addEventListener("click", exportActiveNote);
    el.insightsButton.addEventListener("click", refreshInsights);

    el.previewTheme.addEventListener("change", () => {
      state.previewTheme = normalizeTheme(el.previewTheme.value);
      renderPreview();
      persistState();
    });

    el.autocompleteChip.addEventListener("click", acceptAutocomplete);

    document.addEventListener("keydown", (event) => {
      if (event.ctrlKey && event.key.toLowerCase() === "k") {
        event.preventDefault();
        openPalette();
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
        .slice(0, 120);

      state.activeId = parsed.activeId || state.notes[0].id;
      state.previewTheme = normalizeTheme(parsed.previewTheme || "studio");
      state.lastSource = (parsed.lastSource || "local").toString();
    } catch {
      seedNotes();
    }
  }

  function seedNotes() {
    state.notes = [
      {
        id: createId(),
        title: "Launch plan",
        content: "# JD.Writer launch\n\n## Today\n- Build client-only GitHub Pages studio\n- Keep markdown preview themes\n- Ship local AI heuristics\n\n## Risks\n- Keep contrast deterministic\n",
        updatedAt: new Date().toISOString()
      },
      {
        id: createId(),
        title: "Idea dump",
        content: "# Idea dump\n\n- Friction log panel\n- Plugin sandbox for transforms\n- Bring voice capture later\n",
        updatedAt: new Date(Date.now() - 1000 * 60 * 9).toISOString()
      }
    ];

    state.activeId = state.notes[0].id;
    state.previewTheme = "studio";
    state.lastSource = "local";
    persistState();
  }

  function persistState() {
    const payload = {
      notes: state.notes,
      activeId: state.activeId,
      previewTheme: state.previewTheme,
      lastSource: state.lastSource
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

  function continueDraft() {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    const generated = buildContinuation(note.content || "");
    note.content = appendToDraft(note.content, generated);
    note.updatedAt = new Date().toISOString();
    state.lastSource = "heuristic";

    renderAll();
    persistState();
    refreshInsights();
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
    insightsTimer = window.setTimeout(() => refreshInsights(), INSIGHT_DEBOUNCE_MS);
  }

  function refreshInsights() {
    const draft = getActiveNote()?.content || "";
    state.insights.hints = buildAssistLines("hints", draft);
    state.insights.help = buildAssistLines("help", draft);
    state.insights.brainstorm = buildAssistLines("brainstorm", draft);
    state.lastSource = "local";
    renderInsights();
    renderHeader();
    persistState();
  }

  function renderInsights() {
    renderInsightList(el.hintsList, state.insights.hints);
    renderInsightList(el.helpList, state.insights.help);
    renderInsightList(el.brainstormList, state.insights.brainstorm);
  }

  function renderInsightList(target, items) {
    target.innerHTML = items.map((item) => `<li>${escapeHtml(item)}</li>`).join("");
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
    const note = getActiveNote();
    if (!note) {
      return;
    }

    note.content = el.content.value;
    note.updatedAt = new Date().toISOString();
    state.lastSource = "local";
    renderAll();
    persistState();
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
          applySlashCommand(command);
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

  function applySlashCommand(command) {
    const note = getActiveNote();
    if (!note) {
      return;
    }

    const context = state.slashContext;
    if (context) {
      el.content.value = replaceRange(el.content.value, context.start, context.start + context.length, "").replace(/\s+$/, "");
    }

    const transformed = command.run(el.content.value || "");
    el.content.value = appendToDraft(el.content.value, transformed.trim());
    note.content = el.content.value;
    note.updatedAt = new Date().toISOString();
    state.lastSource = "heuristic";

    renderAll();
    persistState();
    refreshInsights();
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
          refreshInsights();
        }
      },
      {
        title: "AI continue",
        description: "Append local continuation heuristics.",
        action: continueDraft
      },
      {
        title: "Refresh insights",
        description: "Rebuild hints, help, and brainstorm panels.",
        action: refreshInsights
      },
      {
        title: "Export markdown",
        description: "Download the active note as .md.",
        action: exportActiveNote
      },
      ...slashCommands.map((cmd) => ({
        title: `Run /${cmd.name}`,
        description: cmd.description,
        action: () => applySlashCommand(cmd)
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
    return ["studio", "paper", "terminal", "noir"].includes(value) ? value : "studio";
  }

  function createId() {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }

    return `note-${Math.random().toString(16).slice(2)}-${Date.now().toString(16)}`;
  }

  function escapeHtml(value) {
    return (value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }
})();

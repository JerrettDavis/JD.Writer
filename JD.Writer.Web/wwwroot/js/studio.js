window.JDWriterStudio = {
  _dictationState: {
    recognition: null,
    dotNetRef: null,
    isRunning: false,
    testMode: false
  },
  loadState: function () {
    return window.localStorage.getItem("jdwriter.state.v1");
  },
  saveState: function (serializedState) {
    window.localStorage.setItem("jdwriter.state.v1", serializedState);
  },
  downloadMarkdown: function (filename, content) {
    const blob = new Blob([content], { type: "text/markdown;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
  },
  loadPluginManifest: async function () {
    try {
      const response = await fetch("/plugins/plugins.json", { cache: "no-store" });
      if (!response.ok) {
        return null;
      }

      return await response.text();
    } catch {
      return null;
    }
  },
  isDictationSupported: function () {
    return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
  },
  setDictationTestMode: function (enabled) {
    this._dictationState.testMode = !!enabled;
  },
  startDictation: function (dotNetRef) {
    if (this._dictationState.testMode) {
      this._dictationState.dotNetRef = dotNetRef;
      this._dictationState.isRunning = true;
      this._notifyCaptureStatus("recording");
      return { started: true, reason: "mock" };
    }

    const speechCtor = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!speechCtor) {
      return { started: false, reason: "unsupported" };
    }

    if (this._dictationState.isRunning) {
      return { started: true, reason: "already-running" };
    }

    const recognition = new speechCtor();
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.lang = navigator.language || "en-US";

    this._dictationState.recognition = recognition;
    this._dictationState.dotNetRef = dotNetRef;
    this._dictationState.isRunning = true;

    recognition.onstart = () => this._notifyCaptureStatus("recording");
    recognition.onend = () => {
      this._dictationState.isRunning = false;
      this._dictationState.recognition = null;
      this._notifyTranscriptInterim("");
      this._notifyCaptureStatus("stopped");
    };
    recognition.onerror = (event) => {
      this._dictationState.isRunning = false;
      this._dictationState.recognition = null;
      this._notifyTranscriptInterim("");
      this._notifyCaptureStatus("error:" + (event && event.error ? event.error : "unknown"));
    };
    recognition.onresult = (event) => {
      let finalText = "";
      let interimText = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        if (!result || !result[0] || !result[0].transcript) {
          continue;
        }

        const fragment = result[0].transcript.trim();
        if (fragment.length === 0) {
          continue;
        }

        if (result.isFinal) {
          finalText += fragment + " ";
        } else {
          interimText += fragment + " ";
        }
      }

      this._notifyTranscriptInterim(interimText.trim());

      const finalized = finalText.trim();
      if (finalized.length > 0) {
        this._notifyTranscriptFinalized(finalized);
      }
    };

    try {
      recognition.start();
      return { started: true };
    } catch {
      this._dictationState.recognition = null;
      this._dictationState.isRunning = false;
      return { started: false, reason: "start-failed" };
    }
  },
  stopDictation: function () {
    this._dictationState.isRunning = false;
    this._notifyTranscriptInterim("");

    if (this._dictationState.recognition) {
      try {
        this._dictationState.recognition.stop();
      } catch {
        // no-op
      }
      this._dictationState.recognition = null;
    }

    this._notifyCaptureStatus("stopped");
  },
  emitTestInterimTranscript: function (text) {
    if (!this._dictationState.testMode) {
      return false;
    }

    const trimmed = (text || "").trim();
    if (trimmed.length === 0) {
      return false;
    }

    if (!this._dictationState.isRunning) {
      this._dictationState.isRunning = true;
      this._notifyCaptureStatus("recording");
    }

    this._notifyTranscriptInterim(trimmed);
    return true;
  },
  emitTestFinalTranscript: function (text) {
    if (!this._dictationState.testMode) {
      return false;
    }

    const trimmed = (text || "").trim();
    if (trimmed.length === 0) {
      return false;
    }

    if (!this._dictationState.isRunning) {
      this._dictationState.isRunning = true;
      this._notifyCaptureStatus("recording");
    }

    this._notifyTranscriptFinalized(trimmed);
    this._notifyTranscriptInterim("");
    return true;
  },
  emitTestTranscript: function (text) {
    if (!this.emitTestInterimTranscript(text)) {
      return false;
    }

    const trimmed = (text || "").trim();
    window.setTimeout(() => {
      this.emitTestFinalTranscript(trimmed);
    }, 0);
    return true;
  },
  insertTextAtCursor: function (editor, text, options) {
    return this._insertOrReplaceText(editor, text, null, null, options);
  },
  replaceTextRange: function (editor, start, end, text, options) {
    return this._insertOrReplaceText(editor, text, start, end, options);
  },
  _insertOrReplaceText: function (editor, text, start, end, options) {
    const target = editor && typeof editor.value === "string"
      ? editor
      : document.querySelector("textarea.editor-input");

    if (!target) {
      return { value: "", start: 0, end: 0 };
    }

    const hasExplicitRange = Number.isInteger(start) && Number.isInteger(end) && end >= start;
    const safeText = (text || "").trim();
    if (safeText.length === 0 && !hasExplicitRange) {
      return { value: target.value || "", start: 0, end: 0 };
    }

    const baseStart = hasExplicitRange
      ? Math.max(0, Math.min(start, target.value.length))
      : (typeof target.selectionStart === "number" ? target.selectionStart : target.value.length);
    const rawEnd = hasExplicitRange
      ? Math.max(baseStart, Math.min(end, target.value.length))
      : (typeof target.selectionEnd === "number" ? target.selectionEnd : target.value.length);

    const prefix = target.value.slice(0, baseStart);
    const suffix = target.value.slice(rawEnd);

    let insertion = safeText;
    if (safeText.length > 0) {
      if (prefix.length > 0 && !prefix.endsWith("\n") && !prefix.endsWith(" ")) {
        insertion = " " + insertion;
      }
      if (suffix.length > 0 && !suffix.startsWith("\n") && !suffix.startsWith(" ")) {
        insertion += " ";
      }
    }

    target.value = prefix + insertion + suffix;
    const rangeStart = prefix.length;
    const rangeEnd = rangeStart + insertion.length;
    target.setSelectionRange(rangeEnd, rangeEnd);

    const shouldDispatchInput = !options || options.dispatchInput !== false;
    if (shouldDispatchInput) {
      target.dispatchEvent(new Event("input", { bubbles: true }));
    }

    return {
      value: target.value,
      start: rangeStart,
      end: rangeEnd
    };
  },
  _notifyCaptureStatus: function (status) {
    const ref = this._dictationState.dotNetRef;
    if (!ref || typeof ref.invokeMethodAsync !== "function") {
      return;
    }

    ref.invokeMethodAsync("OnVoiceCaptureStatusChanged", status).catch(() => {});
  },
  _notifyTranscriptInterim: function (text) {
    const ref = this._dictationState.dotNetRef;
    if (!ref || typeof ref.invokeMethodAsync !== "function") {
      return;
    }

    ref.invokeMethodAsync("OnVoiceTranscriptInterim", text || "").catch(() => {});
  },
  _notifyTranscriptFinalized: function (text) {
    const ref = this._dictationState.dotNetRef;
    if (!ref || typeof ref.invokeMethodAsync !== "function") {
      return;
    }

    ref.invokeMethodAsync("OnVoiceTranscriptFinalized", text).catch(() => {});
  }
};

window.JDWriterStudio = {
  _dictationState: {
    recognition: null,
    dotNetRef: null,
    isRunning: false,
    testMode: false
  },
  _audioCaptureState: {
    recorder: null,
    stream: null,
    chunks: [],
    startEpochMs: 0,
    dotNetRef: null,
    isRunning: false
  },
  _stateKey: "jdwriter.state.v1",
  _settingsKey: "jdwriter.settings.v1",
  loadState: function () {
    return window.localStorage.getItem(this._stateKey);
  },
  saveState: function (serializedState) {
    window.localStorage.setItem(this._stateKey, serializedState);
  },
  clearState: function () {
    window.localStorage.removeItem(this._stateKey);
  },
  loadSettings: function () {
    return window.localStorage.getItem(this._settingsKey);
  },
  saveSettings: function (serializedSettings) {
    window.localStorage.setItem(this._settingsKey, serializedSettings);
  },
  clearSettings: function () {
    window.localStorage.removeItem(this._settingsKey);
  },
  isAudioCaptureSupported: function () {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
  },
  startAudioCapture: async function (dotNetRef) {
    if (this._dictationState.testMode) {
      this._audioCaptureState.dotNetRef = dotNetRef;
      this._audioCaptureState.isRunning = true;
      this._audioCaptureState.startEpochMs = Date.now();
      this._notifyAudioCaptureStatus("recording");
      return { started: true, reason: "mock" };
    }

    if (this._audioCaptureState.isRunning) {
      return { started: true, reason: "already-running" };
    }

    if (!this.isAudioCaptureSupported()) {
      this._notifyAudioCaptureStatus("unsupported");
      return { started: false, reason: "unsupported" };
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mimeType = this._resolvePreferredAudioMimeType();
      const recorder = mimeType
        ? new MediaRecorder(stream, { mimeType })
        : new MediaRecorder(stream);

      this._audioCaptureState.stream = stream;
      this._audioCaptureState.recorder = recorder;
      this._audioCaptureState.chunks = [];
      this._audioCaptureState.startEpochMs = Date.now();
      this._audioCaptureState.dotNetRef = dotNetRef;
      this._audioCaptureState.isRunning = true;

      recorder.ondataavailable = (event) => {
        if (event && event.data && event.data.size > 0) {
          this._audioCaptureState.chunks.push(event.data);
        }
      };

      recorder.onstop = () => {
        this._finalizeAudioCapture().catch(() => {});
      };

      recorder.onerror = () => {
        this._notifyAudioCaptureStatus("error");
        this.stopAudioCapture().catch(() => {});
      };

      recorder.start();
      this._notifyAudioCaptureStatus("recording");
      return { started: true };
    } catch {
      this._notifyAudioCaptureStatus("error");
      this._resetAudioCaptureState();
      return { started: false, reason: "start-failed" };
    }
  },
  stopAudioCapture: async function () {
    if (this._dictationState.testMode) {
      if (!this._audioCaptureState.isRunning) {
        return { stopped: true, reason: "already-stopped" };
      }

      const durationSeconds = Math.max(0.05, (Date.now() - this._audioCaptureState.startEpochMs) / 1000);
      const payload = {
        fileName: this._buildAudioFileName("wav"),
        mimeType: "audio/wav",
        durationSeconds: durationSeconds,
        sizeBytes: 48,
        dataUrl: "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAESsAACJWAAACABAAZGF0YQAAAAA="
      };
      this._notifyAudioCaptured(payload);
      this._notifyAudioCaptureStatus("stopped");
      this._resetAudioCaptureState();
      return { stopped: true, reason: "mock" };
    }

    if (!this._audioCaptureState.isRunning) {
      return { stopped: true, reason: "already-stopped" };
    }

    const recorder = this._audioCaptureState.recorder;
    if (!recorder) {
      this._resetAudioCaptureState();
      this._notifyAudioCaptureStatus("stopped");
      return { stopped: true, reason: "missing-recorder" };
    }

    if (recorder.state === "inactive") {
      await this._finalizeAudioCapture();
      return { stopped: true, reason: "inactive" };
    }

    recorder.stop();
    return { stopped: true };
  },
  _resolvePreferredAudioMimeType: function () {
    const candidates = [
      "audio/webm;codecs=opus",
      "audio/webm",
      "audio/ogg;codecs=opus",
      "audio/mp4"
    ];
    for (const candidate of candidates) {
      if (window.MediaRecorder && typeof window.MediaRecorder.isTypeSupported === "function" && window.MediaRecorder.isTypeSupported(candidate)) {
        return candidate;
      }
    }

    return "";
  },
  _buildAudioFileName: function (extension) {
    const stamp = new Date().toISOString().replace(/[^\d]/g, "").slice(0, 14);
    return `jdwriter-voice-${stamp}.${extension || "webm"}`;
  },
  _finalizeAudioCapture: async function () {
    if (!this._audioCaptureState.isRunning) {
      this._resetAudioCaptureState();
      return;
    }

    const chunks = this._audioCaptureState.chunks || [];
    const recorder = this._audioCaptureState.recorder;
    const mimeType = recorder && recorder.mimeType ? recorder.mimeType : "audio/webm";
    const blob = new Blob(chunks, { type: mimeType });
    const dataUrl = await this._blobToDataUrl(blob);
    const extension = mimeType.includes("ogg")
      ? "ogg"
      : mimeType.includes("mp4")
        ? "mp4"
        : "webm";
    const durationSeconds = Math.max(0.05, (Date.now() - this._audioCaptureState.startEpochMs) / 1000);

    this._notifyAudioCaptured({
      fileName: this._buildAudioFileName(extension),
      mimeType: mimeType,
      durationSeconds: durationSeconds,
      sizeBytes: blob.size,
      dataUrl: dataUrl
    });
    this._notifyAudioCaptureStatus("stopped");
    this._stopAudioStreamTracks();
    this._resetAudioCaptureState();
  },
  _blobToDataUrl: function (blob) {
    return new Promise((resolve, reject) => {
      try {
        const reader = new FileReader();
        reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
        reader.onerror = () => reject(new Error("failed-to-read-audio-blob"));
        reader.readAsDataURL(blob);
      } catch (error) {
        reject(error);
      }
    });
  },
  _stopAudioStreamTracks: function () {
    const stream = this._audioCaptureState.stream;
    if (!stream || !stream.getTracks) {
      return;
    }

    for (const track of stream.getTracks()) {
      try {
        track.stop();
      } catch {
        // no-op
      }
    }
  },
  _resetAudioCaptureState: function () {
    this._audioCaptureState.recorder = null;
    this._audioCaptureState.stream = null;
    this._audioCaptureState.chunks = [];
    this._audioCaptureState.startEpochMs = 0;
    this._audioCaptureState.isRunning = false;
  },
  applySiteTheme: function (themePreference) {
    const root = document.documentElement;
    const next = (themePreference || "").toLowerCase();
    if (next === "light" || next === "dark") {
      root.setAttribute("data-site-theme", next);
      return next;
    }

    root.removeAttribute("data-site-theme");
    return "system";
  },
  applyReducedMotion: function (enabled) {
    const root = document.documentElement;
    if (enabled) {
      root.setAttribute("data-reduce-motion", "true");
    } else {
      root.removeAttribute("data-reduce-motion");
    }
  },
  applySavedPreferences: function () {
    const raw = this.loadSettings();
    if (!raw) {
      this.applySiteTheme("system");
      this.applyReducedMotion(false);
      return;
    }

    try {
      const parsed = JSON.parse(raw);
      this.applySiteTheme(parsed && parsed.themePreference ? parsed.themePreference : "system");
      this.applyReducedMotion(!!(parsed && parsed.reduceMotion));
    } catch {
      this.applySiteTheme("system");
      this.applyReducedMotion(false);
    }
  },
  downloadMarkdown: function (filename, content) {
    this.downloadText(filename, content, "text/markdown;charset=utf-8");
  },
  downloadText: function (filename, content, mimeType) {
    const blob = new Blob([content], { type: mimeType || "text/plain;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
  },
  downloadDataUrl: function (filename, dataUrl) {
    if (!dataUrl) {
      return false;
    }

    const link = document.createElement("a");
    link.href = dataUrl;
    link.download = filename || "recording";
    document.body.appendChild(link);
    link.click();
    link.remove();
    return true;
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
      this.stopAudioCapture().catch(() => {});
      this._notifyCaptureStatus("stopped");
    };
    recognition.onerror = (event) => {
      this._dictationState.isRunning = false;
      this._dictationState.recognition = null;
      this._notifyTranscriptInterim("");
      this.stopAudioCapture().catch(() => {});
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
    this.stopAudioCapture().catch(() => {});

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
  },
  _notifyAudioCaptureStatus: function (status) {
    const ref = this._audioCaptureState.dotNetRef || this._dictationState.dotNetRef;
    if (!ref || typeof ref.invokeMethodAsync !== "function") {
      return;
    }

    ref.invokeMethodAsync("OnVoiceAudioCaptureStatusChanged", status || "").catch(() => {});
  },
  _notifyAudioCaptured: function (payload) {
    const ref = this._audioCaptureState.dotNetRef || this._dictationState.dotNetRef;
    if (!ref || typeof ref.invokeMethodAsync !== "function") {
      return;
    }

    ref.invokeMethodAsync("OnVoiceAudioCaptured", payload || {}).catch(() => {});
  }
};

window.JDWriterStudio.applySavedPreferences();

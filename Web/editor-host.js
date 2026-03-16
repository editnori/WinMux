(function () {
  const editorRoot = document.getElementById('editor-root');
  const diffRoot = document.getElementById('diff-root');
  const emptyState = document.getElementById('empty-state');
  const hasBridge = !!(window.chrome && window.chrome.webview);
  const initialTheme = (() => {
    try {
      return new URLSearchParams(window.location.search).get('theme') === 'light' ? 'light' : 'dark';
    } catch {
      return 'dark';
    }
  })();

  let editor = null;
  let compareEditor = null;
  let currentTheme = initialTheme;
  let model = null;
  let originalModel = null;
  let modifiedModel = null;
  let pendingDocument = null;
  let suppressChange = false;
  let changeTimer = null;
  let resizeObserver = null;
  let layoutFrame = 0;
  let activeMode = 'file';

  function send(type, payload = {}) {
    if (!hasBridge) {
      return;
    }

    window.chrome.webview.postMessage({ type, ...payload });
  }

  function setEmptyState(visible, title, body) {
    emptyState.classList.toggle('hidden', !visible);
    if (title) {
      emptyState.querySelector('.empty-title').textContent = title;
    }
    if (body) {
      emptyState.querySelector('.empty-body').textContent = body;
    }
  }

  function normalizeTheme(theme) {
    return theme === 'light' ? 'winmux-light' : 'winmux-dark';
  }

  function setDomTheme(theme) {
    const resolved = theme === 'light' ? 'light' : 'dark';
    currentTheme = resolved;
    document.documentElement.dataset.theme = resolved;
    document.body.dataset.theme = resolved;
    return resolved;
  }

  function resolveEol(lineEnding) {
    return lineEnding === '\r\n'
      ? monaco.editor.EndOfLineSequence.CRLF
      : monaco.editor.EndOfLineSequence.LF;
  }

  function showMode(mode) {
    activeMode = mode === 'compare' ? 'compare' : 'file';
    editorRoot.classList.toggle('hidden', activeMode !== 'file');
    diffRoot.classList.toggle('hidden', activeMode !== 'compare');
  }

  function queueLayout() {
    if (layoutFrame) {
      return;
    }

    layoutFrame = window.requestAnimationFrame(() => {
      layoutFrame = 0;
      const target = activeMode === 'compare' ? compareEditor : editor;
      const targetRoot = activeMode === 'compare' ? diffRoot : editorRoot;
      if (!target) {
        return;
      }

      target.layout({
        width: Math.max(0, targetRoot.clientWidth),
        height: Math.max(0, targetRoot.clientHeight),
      });
    });
  }

  function applyStandardDocument(payload, language, readOnly, lineNumbers, renderLineHighlight) {
    const nextText = payload.text || '';
    if (!model) {
      model = monaco.editor.createModel(nextText, language);
      editor.setModel(model);
    } else {
      monaco.editor.setModelLanguage(model, language);
      if (model.getValue() !== nextText) {
        model.setValue(nextText);
      }
    }

    model.setEOL(resolveEol(payload.lineEnding));
    editor.updateOptions({ readOnly, lineNumbers, renderLineHighlight });
    showMode('file');
    setEmptyState(
      !payload.path && !nextText,
      payload.emptyTitle || 'No file selected',
      payload.emptyBody || 'Use the Files tab in the inspector to open a file.',
    );
  }

  function applyCompareDocument(payload, language, readOnly, lineNumbers, renderLineHighlight) {
    const originalText = payload.originalText || '';
    const modifiedText = payload.modifiedText || '';
    const hasComparableContent = !!(originalText || modifiedText);

    if (!originalModel) {
      originalModel = monaco.editor.createModel(originalText, language);
    } else {
      monaco.editor.setModelLanguage(originalModel, language);
      if (originalModel.getValue() !== originalText) {
        originalModel.setValue(originalText);
      }
    }

    if (!modifiedModel) {
      modifiedModel = monaco.editor.createModel(modifiedText, language);
    } else {
      monaco.editor.setModelLanguage(modifiedModel, language);
      if (modifiedModel.getValue() !== modifiedText) {
        modifiedModel.setValue(modifiedText);
      }
    }

    originalModel.setEOL(resolveEol(payload.lineEnding));
    modifiedModel.setEOL(resolveEol(payload.lineEnding));
    compareEditor.setModel({
      original: originalModel,
      modified: modifiedModel,
    });
    compareEditor.updateOptions({
      readOnly,
      lineNumbers,
      renderLineHighlight,
      renderSideBySide: false,
      renderMarginRevertIcon: false,
      diffWordWrap: 'on',
      hideUnchangedRegions: {
        enabled: false,
      },
      experimental: {
        useTrueInlineView: true,
      },
    });
    showMode('compare');
    setEmptyState(
      !hasComparableContent,
      payload.emptyTitle || 'No patch selected',
      payload.emptyBody || 'Select a changed file from the review list.',
    );
  }

  function applyDocument(payload) {
    pendingDocument = payload;
    if ((!editor && !compareEditor) || !window.monaco) {
      return;
    }

    const mode = payload.mode === 'compare' ? 'compare' : 'file';
    const language = payload.language || 'plaintext';
    const readOnly = !!payload.readOnly;
    const theme = payload.theme || currentTheme;
    const lineNumbers = payload.lineNumbers === 'off' ? 'off' : 'on';
    const renderLineHighlight = payload.renderLineHighlight || 'gutter';

    suppressChange = true;
    monaco.editor.setTheme(normalizeTheme(theme));
    setDomTheme(theme);

    if (mode === 'compare') {
      applyCompareDocument(payload, language, readOnly, lineNumbers, renderLineHighlight);
    } else {
      applyStandardDocument(payload, language, readOnly, lineNumbers, renderLineHighlight);
    }

    queueLayout();
    suppressChange = false;
    send('documentApplied', { requestId: payload.requestId || '' });
  }

  function scheduleChange() {
    if (suppressChange || !editor || activeMode !== 'file') {
      return;
    }

    clearTimeout(changeTimer);
    changeTimer = setTimeout(() => {
      send('contentChanged', { text: editor.getValue() });
    }, 120);
  }

  function createEditors() {
    monaco.editor.defineTheme('winmux-light', {
      base: 'vs',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': '#FFFFFF',
        'editor.lineHighlightBackground': '#F8FAFC',
        'editorLineNumber.foreground': '#A1A1AA',
        'editorLineNumber.activeForeground': '#18181B',
        'editorOverviewRuler.border': '#00000000',
        'diffEditor.diagonalFill': '#00000000',
        'diffEditor.insertedTextBackground': '#BBF7D066',
        'diffEditor.removedTextBackground': '#FECACA88',
        'diffEditor.insertedLineBackground': '#ECFDF3',
        'diffEditor.removedLineBackground': '#FEF2F2',
        'diffEditorGutter.insertedLineBackground': '#86EFAC',
        'diffEditorGutter.removedLineBackground': '#FCA5A5',
      },
    });
    monaco.editor.defineTheme('winmux-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': '#111214',
        'editor.lineHighlightBackground': '#17191E',
        'editorLineNumber.foreground': '#71717A',
        'editorLineNumber.activeForeground': '#FAFAFA',
        'editorOverviewRuler.border': '#00000000',
        'diffEditor.diagonalFill': '#00000000',
        'diffEditor.insertedTextBackground': '#16653466',
        'diffEditor.removedTextBackground': '#7F1D1D80',
        'diffEditor.insertedLineBackground': '#0F2217',
        'diffEditor.removedLineBackground': '#271314',
        'diffEditorGutter.insertedLineBackground': '#22C55E',
        'diffEditorGutter.removedLineBackground': '#F87171',
      },
    });

    editor = monaco.editor.create(editorRoot, {
      automaticLayout: false,
      glyphMargin: false,
      lineNumbers: 'on',
      minimap: { enabled: false },
      overviewRulerBorder: false,
      renderLineHighlight: 'gutter',
      roundedSelection: false,
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: false },
      fontFamily: '"Cascadia Code", "Consolas", monospace',
      fontSize: 13,
      lineHeight: 20,
      padding: { top: 12, bottom: 18 },
      tabSize: 4,
      wordWrap: 'off',
      readOnly: true,
      theme: normalizeTheme(currentTheme),
    });

    compareEditor = monaco.editor.createDiffEditor(diffRoot, {
      automaticLayout: false,
      glyphMargin: false,
      lineNumbers: 'on',
      minimap: { enabled: false },
      overviewRulerBorder: false,
      renderOverviewRuler: true,
      renderLineHighlight: 'line',
      roundedSelection: false,
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: false },
      fontFamily: '"Cascadia Code", "Consolas", monospace',
      fontSize: 13,
      lineHeight: 20,
      renderSideBySide: false,
      renderMarginRevertIcon: false,
      diffWordWrap: 'on',
      hideUnchangedRegions: {
        enabled: false,
      },
      experimental: {
        useTrueInlineView: true,
      },
      originalEditable: false,
      readOnly: true,
      theme: normalizeTheme(currentTheme),
    });
    compareEditor.getOriginalEditor().updateOptions({
      lineDecorationsWidth: 12,
      padding: { top: 12, bottom: 18 },
      readOnly: true,
    });
    compareEditor.getModifiedEditor().updateOptions({
      lineDecorationsWidth: 12,
      padding: { top: 12, bottom: 18 },
      readOnly: true,
    });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      send('saveRequested');
    });

    editor.onDidChangeModelContent(scheduleChange);
    resizeObserver = new ResizeObserver(() => {
      queueLayout();
    });
    resizeObserver.observe(editorRoot.parentElement || editorRoot);
    applyDocument(pendingDocument || {
      mode: 'file',
      path: '',
      text: '',
      originalText: '',
      modifiedText: '',
      language: 'plaintext',
      theme: currentTheme,
      lineEnding: '\n',
      readOnly: true,
      lineNumbers: 'on',
      renderLineHighlight: 'gutter',
    });

    send('ready');
  }

  window.__winmuxEditorHost = {
    setDocument(payload) {
      applyDocument(payload || {});
    },
    setTheme(theme) {
      const resolvedTheme = setDomTheme(theme);
      if (window.monaco) {
        monaco.editor.setTheme(normalizeTheme(resolvedTheme));
      }
    },
    focus() {
      if (activeMode === 'compare') {
        compareEditor?.focus();
      } else {
        editor?.focus();
      }
    },
    layout() {
      queueLayout();
    },
  };

  if (hasBridge) {
    window.chrome.webview.addEventListener('message', (event) => {
      const raw = event?.data;
      let message = raw || {};
      if (typeof raw === 'string') {
        try {
          message = JSON.parse(raw);
        } catch {
          message = {};
        }
      }

      switch ((message.type || '').toLowerCase()) {
        case 'document':
          applyDocument(message.payload || {});
          break;
      }
    });
  }

  setDomTheme(initialTheme);
  require.config({ paths: { vs: './vendor/monaco/vs' } });
  require(['vs/editor/editor.main'], createEditors);
})();

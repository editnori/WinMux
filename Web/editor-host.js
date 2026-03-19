(function () {
  const editorRoot = document.getElementById('editor-root');
  const diffRoot = document.getElementById('diff-root');
  const emptyState = document.getElementById('empty-state');
  const hasBridge = !!(window.chrome && window.chrome.webview);
  const hostState = (window.__winmuxEditorHostState = window.__winmuxEditorHostState || {
    ready: false,
    hasBridge,
    lastAppliedRequestId: '',
    lastAppliedPath: '',
    activeMode: 'file',
  });
  const initialTheme = (() => {
    try {
      return new URLSearchParams(window.location.search).get('theme') === 'light' ? 'light' : 'dark';
    } catch {
      return 'dark';
    }
  })();
  const monacoThemeName = 'winmux-shell';

  let editor = null;
  let compareEditor = null;
  let diffNavigator = null;
  let currentTheme = initialTheme;
  let currentPalette = buildDefaultPalette(initialTheme);
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

  function normalizeTheme() {
    return monacoThemeName;
  }

  function buildDefaultPalette(theme) {
    if (theme === 'light') {
      return {
        background: '#F9FBFD',
        text: '#1A1E23',
        subtext: '#39424D',
        faint: '#55606D',
        lineHighlight: '#F2F6FA',
        insertedTextBackground: '#16A34A40',
        removedTextBackground: '#DC26263A',
        insertedLineBackground: '#E8F5ED',
        removedLineBackground: '#F9E8EA',
        insertedGutterBackground: '#2A7A53',
        removedGutterBackground: '#B75162',
      };
    }

    return {
      background: '#101216',
      text: '#F3F4F6',
      subtext: '#A7ADB7',
      faint: '#7A808B',
      lineHighlight: '#17191E',
      insertedTextBackground: '#4ADE8036',
      removedTextBackground: '#F8717136',
      insertedLineBackground: '#183123',
      removedLineBackground: '#341A20',
      insertedGutterBackground: '#62C58D',
      removedGutterBackground: '#F08A8A',
    };
  }

  function buildMonacoTheme(theme, palette) {
    return {
      base: theme === 'light' ? 'vs' : 'vs-dark',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': palette.background,
        'editor.lineHighlightBackground': palette.lineHighlight,
        'editorLineNumber.foreground': palette.faint,
        'editorLineNumber.activeForeground': palette.text,
        'editorOverviewRuler.border': '#00000000',
        'diffEditor.diagonalFill': '#00000000',
        'diffEditor.insertedTextBackground': palette.insertedTextBackground,
        'diffEditor.removedTextBackground': palette.removedTextBackground,
        'diffEditor.insertedTextBorder': '#00000000',
        'diffEditor.removedTextBorder': '#00000000',
        'diffEditor.insertedLineBackground': palette.insertedLineBackground,
        'diffEditor.removedLineBackground': palette.removedLineBackground,
        'diffEditorGutter.insertedLineBackground': palette.insertedGutterBackground,
        'diffEditorGutter.removedLineBackground': palette.removedGutterBackground,
      },
    };
  }

  function applyPalette(palette, theme = currentTheme) {
    currentPalette = {
      ...buildDefaultPalette(theme),
      ...(palette || {}),
    };

    const root = document.documentElement;
    root.style.setProperty('--shell-background', currentPalette.background);
    root.style.setProperty('--shell-text', currentPalette.text);
    root.style.setProperty('--shell-subtext', currentPalette.subtext);
    root.style.setProperty('--shell-faint', currentPalette.faint);

    if (window.monaco) {
      monaco.editor.defineTheme(normalizeTheme(theme), buildMonacoTheme(theme, currentPalette));
      monaco.editor.setTheme(normalizeTheme(theme));
    }
  }

  function setDomTheme(theme) {
    const resolved = theme === 'light' ? 'light' : 'dark';
    currentTheme = resolved;
    document.documentElement.dataset.theme = resolved;
    document.body.dataset.theme = resolved;
    document.documentElement.style.colorScheme = resolved;
    return resolved;
  }

  function resolveEol(lineEnding) {
    return lineEnding === '\r\n'
      ? monaco.editor.EndOfLineSequence.CRLF
      : monaco.editor.EndOfLineSequence.LF;
  }

  function showMode(mode) {
    activeMode = mode === 'compare' ? 'compare' : 'file';
    hostState.activeMode = activeMode;
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
      renderSideBySide: true,
      useInlineViewWhenSpaceIsLimited: false,
      renderSideBySideInlineBreakpoint: 0,
      renderMarginRevertIcon: false,
      renderIndicators: true,
      diffWordWrap: 'off',
      hideUnchangedRegions: {
        enabled: false,
      },
      splitViewDefaultRatio: 0.5,
    });
    compareEditor.getModifiedEditor().updateOptions({
      lineDecorationsWidth: 4,
      padding: { top: 8, bottom: 10 },
      lineNumbersMinChars: 1,
      readOnly,
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
    const resolvedTheme = setDomTheme(theme);
    applyPalette(payload.palette || currentPalette, resolvedTheme);

    if (mode === 'compare') {
      applyCompareDocument(payload, language, readOnly, lineNumbers, renderLineHighlight);
    } else {
      applyStandardDocument(payload, language, readOnly, lineNumbers, renderLineHighlight);
    }

    queueLayout();
    suppressChange = false;
    hostState.lastAppliedRequestId = payload.requestId || '';
    hostState.lastAppliedPath = payload.path || '';
    send('documentApplied', { requestId: payload.requestId || '' });
  }

  function scheduleChange() {
    if (suppressChange || !window.monaco) {
      return;
    }

    const activeEditor = activeMode === 'compare'
      ? compareEditor?.getModifiedEditor?.()
      : editor;
    if (!activeEditor || activeEditor.getOption(monaco.editor.EditorOption.readOnly)) {
      return;
    }

    clearTimeout(changeTimer);
    changeTimer = setTimeout(() => {
      send('contentChanged', { text: activeEditor.getValue() });
    }, 120);
  }

  function createEditors() {
    hostState.ready = false;
    applyPalette(currentPalette, currentTheme);

    editor = monaco.editor.create(editorRoot, {
      automaticLayout: false,
      bracketPairColorization: { enabled: false },
      glyphMargin: false,
      guides: {
        bracketPairs: false,
        highlightActiveBracketPair: false,
        highlightActiveIndentation: false,
        indentation: false,
      },
      lineNumbers: 'on',
      minimap: { enabled: false },
      occurrencesHighlight: 'off',
      overviewRulerBorder: false,
      renderLineHighlight: 'gutter',
      selectionHighlight: false,
      roundedSelection: false,
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: false },
      fontFamily: '"Cascadia Code", "Consolas", monospace',
      fontSize: 13,
      lineHeight: 20,
      lineNumbersMinChars: 1,
      padding: { top: 8, bottom: 10 },
      tabSize: 4,
      wordWrap: 'off',
      readOnly: true,
      theme: normalizeTheme(currentTheme),
    });

    compareEditor = monaco.editor.createDiffEditor(diffRoot, {
      automaticLayout: false,
      bracketPairColorization: { enabled: false },
      glyphMargin: false,
      guides: {
        bracketPairs: false,
        highlightActiveBracketPair: false,
        highlightActiveIndentation: false,
        indentation: false,
      },
      lineNumbers: 'on',
      minimap: { enabled: false },
      occurrencesHighlight: 'off',
      overviewRulerBorder: false,
      renderOverviewRuler: false,
      renderLineHighlight: 'none',
      roundedSelection: false,
      selectionHighlight: false,
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: false },
      fontFamily: '"Cascadia Code", "Consolas", monospace',
      fontSize: 13,
      lineHeight: 20,
      lineNumbersMinChars: 1,
      renderSideBySide: true,
      useInlineViewWhenSpaceIsLimited: false,
      renderSideBySideInlineBreakpoint: 0,
      renderMarginRevertIcon: false,
      renderIndicators: false,
      diffWordWrap: 'off',
      hideUnchangedRegions: {
        enabled: false,
      },
      splitViewDefaultRatio: 0.5,
      originalEditable: false,
      readOnly: true,
      theme: normalizeTheme(currentTheme),
    });
    compareEditor.getOriginalEditor().updateOptions({
      lineDecorationsWidth: 4,
      padding: { top: 8, bottom: 10 },
      lineNumbersMinChars: 1,
      guides: {
        bracketPairs: false,
        highlightActiveBracketPair: false,
        highlightActiveIndentation: false,
        indentation: false,
      },
      matchBrackets: 'never',
      occurrencesHighlight: 'off',
      readOnly: true,
      renderLineHighlight: 'none',
      selectionHighlight: false,
      wordWrap: 'off',
    });
    compareEditor.getModifiedEditor().updateOptions({
      lineDecorationsWidth: 4,
      padding: { top: 8, bottom: 10 },
      lineNumbersMinChars: 1,
      guides: {
        bracketPairs: false,
        highlightActiveBracketPair: false,
        highlightActiveIndentation: false,
        indentation: false,
      },
      matchBrackets: 'never',
      occurrencesHighlight: 'off',
      readOnly: false,
      renderLineHighlight: 'none',
      selectionHighlight: false,
      wordWrap: 'off',
    });
    diffNavigator = monaco.editor.createDiffNavigator(compareEditor, {
      followsCaret: true,
      ignoreCharChanges: false,
      alwaysRevealFirst: true,
    });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      send('saveRequested');
    });
    compareEditor.getModifiedEditor().addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      send('saveRequested');
    });

    editor.onDidChangeModelContent(scheduleChange);
    compareEditor.getModifiedEditor().onDidChangeModelContent(scheduleChange);
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

    hostState.ready = true;
    send('ready');
  }

  window.__winmuxEditorHost = {
    setDocument(payload) {
      applyDocument(payload || {});
    },
    setTheme(theme, palette) {
      const resolvedTheme = setDomTheme(theme);
      applyPalette(palette || currentPalette, resolvedTheme);
    },
    applyShellPalette(palette) {
      applyPalette(palette || currentPalette, currentTheme);
    },
    focus() {
      if (activeMode === 'compare') {
        compareEditor?.getModifiedEditor?.().focus();
      } else {
        editor?.focus();
      }
    },
    goToPreviousDiff() {
      diffNavigator?.previous();
    },
    goToNextDiff() {
      diffNavigator?.next();
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
  applyPalette(currentPalette, initialTheme);
  require.config({ paths: { vs: './vendor/monaco/vs' } });
  require(['vs/editor/editor.main'], createEditors);
})();

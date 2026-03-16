(function () {
  const root = document.getElementById('editor-root');
  const emptyState = document.getElementById('empty-state');
  const hasBridge = !!(window.chrome && window.chrome.webview);
  let editor = null;
  let model = null;
  let pendingDocument = null;
  let suppressChange = false;
  let changeTimer = null;

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
    return theme === 'light' ? 'vs' : 'vs-dark';
  }

  function resolveEol(lineEnding) {
    return lineEnding === '\r\n'
      ? monaco.editor.EndOfLineSequence.CRLF
      : monaco.editor.EndOfLineSequence.LF;
  }

  function applyDocument(payload) {
    pendingDocument = payload;
    if (!editor || !window.monaco) {
      return;
    }

    const nextText = payload.text || '';
    const language = payload.language || 'plaintext';
    const readOnly = !!payload.readOnly;
    const theme = payload.theme || 'dark';

    suppressChange = true;

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
    editor.updateOptions({ readOnly });
    monaco.editor.setTheme(normalizeTheme(theme));
    document.body.dataset.theme = theme;
    setEmptyState(!payload.path, 'No file selected', 'Use the Files tab in the inspector to open a file.');
    suppressChange = false;
  }

  function scheduleChange() {
    if (suppressChange || !editor) {
      return;
    }

    clearTimeout(changeTimer);
    changeTimer = setTimeout(() => {
      send('contentChanged', { text: editor.getValue() });
    }, 120);
  }

  function createEditor() {
    editor = monaco.editor.create(root, {
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
      theme: 'vs-dark',
    });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      send('saveRequested');
    });

    editor.onDidChangeModelContent(scheduleChange);
    applyDocument(pendingDocument || {
      path: '',
      text: '',
      language: 'plaintext',
      theme: 'dark',
      lineEnding: '\n',
      readOnly: true,
    });

    send('ready');
  }

  window.__winmuxEditorHost = {
    setDocument(payload) {
      applyDocument(payload || {});
    },
    setTheme(theme) {
      document.body.dataset.theme = theme === 'light' ? 'light' : 'dark';
      if (window.monaco) {
        monaco.editor.setTheme(normalizeTheme(theme));
      }
    },
    focus() {
      editor?.focus();
    },
    layout() {
      editor?.layout();
    },
  };

  require.config({ paths: { vs: './vendor/monaco/vs' } });
  require(['vs/editor/editor.main'], createEditor);
})();

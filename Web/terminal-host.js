(function () {
    const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;
    const termRoot = document.getElementById("terminal-root");
    const termStage = document.getElementById("terminal-stage");
    const contextMenu = document.getElementById("terminal-menu");
    const copyMenuItem = document.getElementById("terminal-menu-copy");
    const pasteMenuItem = document.getElementById("terminal-menu-paste");
    const selectAllMenuItem = document.getElementById("terminal-menu-select-all");
    const statusLine = document.getElementById("status-line");
    let fitFrame = 0;
    let fitSettleFrame = 0;
    let pendingForcedFit = false;
    let lastMeasuredWidth = 0;
    let lastMeasuredHeight = 0;
    let lastPostedCols = 0;
    let lastPostedRows = 0;
    let readyPosted = false;
    let activeToolSession = null;
    let appliedToolSession = "none";
    let toolSurfaceVisible = false;
    let activeShellKind = "terminal";
    let currentThemeName = "dark";
    const shellAccents = {
        terminal: {
            dark: { accent: "#c9cdd4", selection: "rgba(201, 205, 212, 0.14)" },
            light: { accent: "#48505a", selection: "rgba(72, 80, 90, 0.12)" },
        },
        wsl: {
            dark: { accent: "#60a5fa", selection: "rgba(96, 165, 250, 0.16)" },
            light: { accent: "#2563eb", selection: "rgba(37, 99, 235, 0.14)" },
        },
        powershell: {
            dark: { accent: "#4ade80", selection: "rgba(74, 222, 128, 0.16)" },
            light: { accent: "#16a34a", selection: "rgba(22, 163, 74, 0.14)" },
        },
        cmd: {
            dark: { accent: "#fbbf24", selection: "rgba(251, 191, 36, 0.16)" },
            light: { accent: "#ca8a04", selection: "rgba(202, 138, 4, 0.14)" },
        },
    };
    const darkThemeBase = {
        background: "#101216",
        foreground: "#f3f4f6",
        cursor: "#60a5fa",
        cursorAccent: "#101216",
        selectionBackground: "rgba(96, 165, 250, 0.16)",
        selectionInactiveBackground: "rgba(167, 173, 183, 0.12)",
        black: "#0c0d10",
        red: "#f87171",
        green: "#4ade80",
        yellow: "#fbbf24",
        blue: "#94a3b8",
        magenta: "#c084fc",
        cyan: "#22d3ee",
        white: "#d4d4d8",
        brightBlack: "#52525b",
        brightRed: "#fca5a5",
        brightGreen: "#86efac",
        brightYellow: "#fcd34d",
        brightBlue: "#cbd5e1",
        brightMagenta: "#d8b4fe",
        brightCyan: "#67e8f9",
        brightWhite: "#ffffff",
    };
    const lightThemeBase = {
        background: "#fbfcfd",
        foreground: "#1b1f24",
        cursor: "#2563eb",
        cursorAccent: "#fbfcfd",
        selectionBackground: "rgba(37, 99, 235, 0.14)",
        selectionInactiveBackground: "rgba(72, 80, 90, 0.1)",
        black: "#1b1f24",
        red: "#dc2626",
        green: "#16a34a",
        yellow: "#ca8a04",
        blue: "#2563eb",
        magenta: "#9333ea",
        cyan: "#0f766e",
        white: "#eef1f4",
        brightBlack: "#59606a",
        brightRed: "#e11d48",
        brightGreen: "#16a34a",
        brightYellow: "#ca8a04",
        brightBlue: "#2563eb",
        brightMagenta: "#a855f7",
        brightCyan: "#0d9488",
        brightWhite: "#fafafa",
    };

    const term = new Terminal({
        allowTransparency: false,
        convertEol: false,
        cursorBlink: true,
        cursorStyle: "bar",
        customGlyphs: true,
        fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
        fontSize: 13,
        letterSpacing: 0,
        lineHeight: 1.05,
        fontWeight: "400",
        fontWeightBold: "600",
        scrollback: 6000,
        theme: darkThemeBase,
        windowsPty: {
            backend: "conpty",
            buildNumber: 22621,
        },
    });

    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(termStage);

    const demoState = {
        cwd: "C:\\Users\\lqassem\\native-terminal-starter",
        input: "",
        ended: false,
    };

    function normalizeShellKind(shellKind) {
        if (!shellKind || typeof shellKind !== "string") {
            return "terminal";
        }

        const normalized = shellKind.trim().toLowerCase();
        return normalized === "wsl" || normalized === "powershell" || normalized === "cmd"
            ? normalized
            : "terminal";
    }

    function buildTheme(themeName, shellKind) {
        const resolvedTheme = themeName === "light" ? "light" : "dark";
        const resolvedShellKind = normalizeShellKind(shellKind);
        const baseTheme = resolvedTheme === "light" ? lightThemeBase : darkThemeBase;
        const accent = shellAccents[resolvedShellKind]?.[resolvedTheme] ?? shellAccents.terminal[resolvedTheme];
        return {
            ...baseTheme,
            cursor: accent.accent,
            selectionBackground: accent.selection,
        };
    }

    function updateChrome() {
        document.body.dataset.shellKind = normalizeShellKind(activeShellKind);
        document.body.dataset.toolSurface = toolSurfaceVisible ? "true" : "false";
    }

    function setTheme(themeName) {
        currentThemeName = themeName === "light" ? "light" : "dark";
        document.body.dataset.theme = currentThemeName;
        term.options.theme = buildTheme(currentThemeName, activeShellKind);
        updateChrome();
    }

    function normalizeToolSession(toolSession) {
        if (!toolSession || typeof toolSession !== "string") {
            return null;
        }

        const normalized = toolSession.trim().toLowerCase();
        return normalized || null;
    }

    function inferCodexToolSession() {
        const sample = `${getBufferTail()}\n${getVisibleText()}`;
        return /(?:context left|\/status\b|weekly limit left|mcp startup incomplete|run \/[a-z]|gpt-[\w.]+|xhigh|xsmall|fast\b)/i.test(sample);
    }

    function syncToolSession(forceFit = false) {
        const inferredToolSession = activeToolSession ?? (inferCodexToolSession() ? "codex" : null);
        const nextToolSession = inferredToolSession === "codex" ? "codex" : "none";
        if (appliedToolSession === nextToolSession) {
            return;
        }

        appliedToolSession = nextToolSession;
        document.body.dataset.toolSession = nextToolSession;
        scheduleFit(forceFit);
    }

    function setToolSession(toolSession) {
        activeToolSession = normalizeToolSession(toolSession);
        syncToolSession(true);
        updateChrome();
    }

    function setToolSurfaceVisible(visible) {
        toolSurfaceVisible = !!visible;
        updateChrome();
    }

    function setSurfaceContext(shellKind) {
        activeShellKind = normalizeShellKind(shellKind);
        term.options.theme = buildTheme(currentThemeName, activeShellKind);
        updateChrome();
    }

    function setWindowFocused(focused) {
        document.body.dataset.focused = focused ? "true" : "false";
    }

    function setTitle(title) {
        const nextTitle = title && title.trim() ? title.trim() : "Native Terminal";
        document.title = nextTitle;
        updateChrome();
    }

    function setStatus(text, visible) {
        statusLine.textContent = text || "";
        statusLine.dataset.visible = visible ? "true" : "false";
    }

    function hideContextMenu() {
        contextMenu.hidden = true;
    }

    function showContextMenu(clientX, clientY) {
        copyMenuItem.disabled = !term.hasSelection();
        contextMenu.hidden = false;

        const maxLeft = Math.max(8, window.innerWidth - contextMenu.offsetWidth - 8);
        const maxTop = Math.max(8, window.innerHeight - contextMenu.offsetHeight - 8);
        contextMenu.style.left = `${Math.min(clientX, maxLeft)}px`;
        contextMenu.style.top = `${Math.min(clientY, maxTop)}px`;
    }

    function post(message) {
        if (bridge) {
            bridge.postMessage(message);
        }
    }

    function postReady() {
        if (!bridge || readyPosted) {
            return false;
        }

        readyPosted = true;
        post({ type: "ready" });
        return true;
    }

    function fitTerminal(force = false) {
        const rect = termStage.getBoundingClientRect();
        const width = Math.round(rect.width);
        const height = Math.round(rect.height);
        if (width <= 2 || height <= 2) {
            return;
        }

        if (!force && width === lastMeasuredWidth && height === lastMeasuredHeight) {
            return;
        }

        fitAddon.fit();
        lastMeasuredWidth = width;
        lastMeasuredHeight = height;

        if (force || term.cols !== lastPostedCols || term.rows !== lastPostedRows) {
            lastPostedCols = term.cols;
            lastPostedRows = term.rows;
            post({
                type: "resize",
                cols: term.cols,
                rows: term.rows,
            });
        }
    }

    function scheduleFit(force = false) {
        pendingForcedFit = pendingForcedFit || force;
        if (fitFrame) {
            cancelAnimationFrame(fitFrame);
        }
        if (fitSettleFrame) {
            cancelAnimationFrame(fitSettleFrame);
        }

        fitFrame = requestAnimationFrame(() => {
            fitFrame = 0;
            fitSettleFrame = requestAnimationFrame(() => {
                fitSettleFrame = 0;
                const shouldForceFit = pendingForcedFit;
                pendingForcedFit = false;
                fitTerminal(shouldForceFit);
            });
        });
    }

    function prompt() {
        return `${demoState.cwd}> `;
    }

    function writePrompt() {
        term.write(prompt());
    }

    function normalizePath(input) {
        if (!input) {
            return demoState.cwd;
        }

        if (/^[a-z]:\\/i.test(input)) {
            return input;
        }

        if (input === "..") {
            const parts = demoState.cwd.split("\\");
            if (parts.length > 2) {
                parts.pop();
                return parts.join("\\");
            }

            return demoState.cwd;
        }

        return `${demoState.cwd}\\${input}`;
    }

    function runDemoCommand(line) {
        const trimmed = line.trim();
        if (!trimmed) {
            return;
        }

        const parts = trimmed.split(/\s+/);
        const command = parts[0].toLowerCase();
        const args = parts.slice(1);

        switch (command) {
            case "help":
                term.writeln("help  cls  pwd  cd  dir  echo  title");
                break;
            case "cls":
                term.clear();
                break;
            case "pwd":
                term.writeln(demoState.cwd);
                break;
            case "cd":
                demoState.cwd = normalizePath(args.join(" "));
                setTitle(demoState.cwd);
                break;
            case "dir":
            case "ls":
                term.writeln("03/14/2026  04:29 AM    <DIR>          Terminal");
                term.writeln("03/14/2026  04:29 AM               999 README.md");
                term.writeln("03/14/2026  04:29 AM             1,508 MainPage.xaml");
                term.writeln('03/14/2026  04:29 AM        1,024,000 "New Bitmap image.bmp"');
                break;
            case "echo":
                term.writeln(args.join(" "));
                break;
            case "title":
                setTitle(args.join(" ") || demoState.cwd);
                break;
            default:
                term.writeln(`'${command}' is not recognized by the demo shell.`);
                break;
        }
    }

    function handleDemoInput(data) {
        if (demoState.ended) {
            return;
        }

        if (data === "\r") {
            term.write("\r\n");
            runDemoCommand(demoState.input);
            demoState.input = "";
            writePrompt();
            return;
        }

        if (data === "\u007f") {
            if (demoState.input.length > 0) {
                demoState.input = demoState.input.slice(0, -1);
                term.write("\b \b");
            }
            return;
        }

        if (data === "\u0003") {
            term.write("^C\r\n");
            demoState.input = "";
            writePrompt();
            return;
        }

        if (data.startsWith("\u001b")) {
            return;
        }

        demoState.input += data;
        term.write(data);
    }

    function bootDemoShell() {
        setStatus("Chrome renderer preview. Native WinUI host uses the real ConPTY bridge.", true);
        setTitle(demoState.cwd);
        writePrompt();
    }

    function handleHostMessage(message) {
        switch (message.type) {
            case "output":
                term.write(message.data || "");
                setStatus("", false);
                syncToolSession(true);
                break;
            case "system":
                if (message.text) {
                    term.writeln(`\r\n${message.text}\r\n`);
                    setStatus(message.text, true);
                    syncToolSession(true);
                }
                break;
            case "exit":
                demoState.ended = true;
                term.writeln("");
                term.writeln("[session ended]");
                setStatus(message.text || "Shell exited", true);
                syncToolSession(true);
                break;
            case "focus":
                window.setTimeout(() => term.focus(), 0);
                break;
            case "fit":
                scheduleFit(true);
                break;
            case "inspect":
                post({
                    type: "state",
                    requestId: message.requestId,
                    cols: term.cols,
                    rows: term.rows,
                    cursorX: term.buffer.active.cursorX,
                    cursorY: term.buffer.active.cursorY,
                    viewportY: term.buffer.active.viewportY,
                    bufferLength: term.buffer.active.length,
                    selection: term.getSelection(),
                    visibleText: getVisibleText(),
                    bufferTail: getBufferTail(),
                    title: document.title,
                    toolSession: activeToolSession,
                });
                break;
            case "setTitle":
                setTitle(message.title);
                break;
            case "setTheme":
                setTheme(message.theme);
                break;
            case "setToolSession":
                setToolSession(message.toolSession);
                setToolSurfaceVisible(message.toolSurfaceVisible);
                break;
            case "setSurfaceContext":
                setSurfaceContext(message.shellKind);
                break;
            default:
                break;
        }
    }

    window.__winmuxTerminalHost = {
        forceReady() {
            return postReady();
        },
        setToolSession(toolSession) {
            setToolSession(toolSession);
        },
        get readyPosted() {
            return readyPosted;
        },
        get activeToolSession() {
            return activeToolSession;
        },
    };

    async function copySelectionToClipboard() {
        const selection = term.getSelection();
        if (!selection) {
            return false;
        }

        if (bridge) {
            post({ type: "copy", data: selection });
        }
        else if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(selection);
        }
        else {
            return false;
        }

        setStatus("Copied selection", true);
        window.setTimeout(() => setStatus("", false), 900);
        return true;
    }

    async function pasteFromClipboard() {
        if (bridge) {
            post({ type: "paste" });
            return true;
        }

        if (!navigator.clipboard || !navigator.clipboard.readText) {
            return false;
        }

        const text = await navigator.clipboard.readText();
        if (!text) {
            return false;
        }

        term.paste(text);
        return true;
    }

    function getVisibleText() {
        const active = term.buffer.active;
        const start = Math.max(0, active.viewportY);
        const end = Math.min(active.length, start + term.rows);
        const lines = [];

        for (let i = start; i < end; i++) {
            const line = active.getLine(i);
            if (line) {
                lines.push(line.translateToString(true));
            }
        }

        return lines.join("\n");
    }

    function getBufferTail() {
        const active = term.buffer.active;
        const start = Math.max(0, active.length - 200);
        const lines = [];

        for (let i = start; i < active.length; i++) {
            const line = active.getLine(i);
            if (line) {
                lines.push(line.translateToString(true));
            }
        }

        return lines.join("\n");
    }

    document.addEventListener("pointerdown", (event) => {
        if (!contextMenu.hidden && contextMenu.contains(event.target)) {
            return;
        }

        hideContextMenu();
        post({ type: "focus" });
        term.focus();
        setWindowFocused(true);
    });

    term.onData((data) => {
        if (bridge) {
            post({ type: "input", data });
            return;
        }

        handleDemoInput(data);
    });

    term.onTitleChange((title) => {
        setTitle(title);
        post({ type: "title", title });
    });

    term.onRender(() => {
        syncToolSession(false);
    });

    window.addEventListener("keydown", (event) => {
        const wantsCopy = (event.ctrlKey || event.metaKey) && ((event.shiftKey && event.key.toLowerCase() === "c") || event.key === "Insert");
        const wantsPaste = (event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === "v"
            || (event.shiftKey && event.key === "Insert");

        if (event.key === "Escape" && !contextMenu.hidden) {
            event.preventDefault();
            hideContextMenu();
            return;
        }

        if (wantsCopy) {
            if (!term.hasSelection()) {
                return;
            }

            event.preventDefault();
            void copySelectionToClipboard();
            return;
        }

        if (!wantsPaste) {
            return;
        }

        event.preventDefault();
        void pasteFromClipboard();
    }, true);

    termRoot.addEventListener("contextmenu", (event) => {
        event.preventDefault();
        term.focus();
        showContextMenu(event.clientX, event.clientY);
    });

    copyMenuItem.addEventListener("click", () => {
        hideContextMenu();
        void copySelectionToClipboard();
    });

    pasteMenuItem.addEventListener("click", () => {
        hideContextMenu();
        void pasteFromClipboard();
    });

    selectAllMenuItem.addEventListener("click", () => {
        hideContextMenu();
        term.selectAll();
        term.focus();
    });

    window.addEventListener("blur", () => {
        hideContextMenu();
        setWindowFocused(false);
    });
    window.addEventListener("focus", () => setWindowFocused(true));
    window.addEventListener("resize", () => {
        hideContextMenu();
    });
    new ResizeObserver(() => scheduleFit()).observe(termStage);
    if (document.fonts && document.fonts.ready && typeof document.fonts.ready.then === "function") {
        document.fonts.ready.then(() => scheduleFit(true));
    }
    if (document.fonts && typeof document.fonts.addEventListener === "function") {
        document.fonts.addEventListener("loadingdone", () => scheduleFit(true));
    }

    requestAnimationFrame(() => {
        setTheme("dark");
        setToolSession(null);
        setToolSurfaceVisible(false);
        setSurfaceContext("terminal");
        setWindowFocused(document.hasFocus());
        syncToolSession(true);
        scheduleFit(true);
        term.focus();

        if (bridge) {
            bridge.addEventListener("message", (event) => handleHostMessage(event.data));
            postReady();
        }
        else {
            bootDemoShell();
        }
    });
})();

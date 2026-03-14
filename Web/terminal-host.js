(function () {
    const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;
    const termRoot = document.getElementById("terminal-root");
    const statusLine = document.getElementById("status-line");
    let fitFrame = 0;
    const darkTheme = {
        background: "#09090b",
        foreground: "#f4f4f5",
        cursor: "#f4f4f5",
        cursorAccent: "#09090b",
        selectionBackground: "rgba(244, 244, 245, 0.14)",
        black: "#050506",
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
    const lightTheme = {
        background: "#ffffff",
        foreground: "#18181b",
        cursor: "#18181b",
        cursorAccent: "#ffffff",
        selectionBackground: "rgba(24, 24, 27, 0.12)",
        black: "#18181b",
        red: "#be123c",
        green: "#15803d",
        yellow: "#a16207",
        blue: "#1d4ed8",
        magenta: "#9333ea",
        cyan: "#0f766e",
        white: "#e4e4e7",
        brightBlack: "#71717a",
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
        convertEol: true,
        cursorBlink: true,
        cursorStyle: "bar",
        fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
        fontSize: 13,
        letterSpacing: 0,
        lineHeight: 1,
        scrollback: 6000,
        theme: darkTheme,
        windowsPty: {
            backend: "conpty",
            buildNumber: 22621,
        },
    });

    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(termRoot);

    const demoState = {
        cwd: "C:\\Users\\lqassem\\native-terminal-starter",
        input: "",
        ended: false,
    };

    function setTheme(themeName) {
        const resolvedTheme = themeName === "light" ? "light" : "dark";
        document.body.dataset.theme = resolvedTheme;
        term.options.theme = resolvedTheme === "light" ? lightTheme : darkTheme;
    }

    function setTitle(title) {
        const nextTitle = title && title.trim() ? title.trim() : "Native Terminal";
        document.title = nextTitle;
    }

    function setStatus(text, visible) {
        statusLine.textContent = text || "";
        statusLine.dataset.visible = visible ? "true" : "false";
    }

    function post(message) {
        if (bridge) {
            bridge.postMessage(message);
        }
    }

    function fitTerminal() {
        fitAddon.fit();
        post({
            type: "resize",
            cols: term.cols,
            rows: term.rows,
        });
    }

    function scheduleFit() {
        if (fitFrame) {
            cancelAnimationFrame(fitFrame);
        }

        fitFrame = requestAnimationFrame(() => {
            fitFrame = 0;
            fitTerminal();
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
                break;
            case "system":
                if (message.text) {
                    term.writeln(`\r\n${message.text}\r\n`);
                    setStatus(message.text, true);
                }
                break;
            case "exit":
                demoState.ended = true;
                term.writeln("");
                term.writeln("[session ended]");
                setStatus(message.text || "Shell exited", true);
                break;
            case "focus":
                window.setTimeout(() => term.focus(), 0);
                break;
            case "fit":
                scheduleFit();
                break;
            case "setTitle":
                setTitle(message.title);
                break;
            case "setTheme":
                setTheme(message.theme);
                break;
            default:
                break;
        }
    }

    function copySelectionToHost() {
        const selection = term.getSelection();
        if (!selection) {
            return;
        }

        post({ type: "copy", data: selection });
        setStatus("Copied selection", true);
        window.setTimeout(() => setStatus("", false), 900);
    }

    document.addEventListener("pointerdown", () => term.focus());

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

    window.addEventListener("keydown", (event) => {
        const wantsCopy = (event.ctrlKey || event.metaKey) && ((event.shiftKey && event.key.toLowerCase() === "c") || event.key === "Insert");
        if (!wantsCopy) {
            return;
        }

        if (!term.hasSelection()) {
            return;
        }

        event.preventDefault();
        copySelectionToHost();
    }, true);

    window.addEventListener("resize", scheduleFit);
    new ResizeObserver(() => scheduleFit()).observe(document.body);
    new ResizeObserver(() => scheduleFit()).observe(termRoot);

    requestAnimationFrame(() => {
        setTheme("dark");
        scheduleFit();
        term.focus();

        if (bridge) {
            bridge.addEventListener("message", (event) => handleHostMessage(event.data));
            post({ type: "ready" });
        }
        else {
            bootDemoShell();
        }
    });
})();

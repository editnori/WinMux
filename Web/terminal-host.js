(function () {
    const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;
    const termRoot = document.getElementById("terminal-root");
    const statusLine = document.getElementById("status-line");

    const term = new Terminal({
        allowTransparency: false,
        convertEol: true,
        cursorBlink: true,
        cursorStyle: "bar",
        fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
        fontSize: 13,
        letterSpacing: 0,
        lineHeight: 1.08,
        scrollback: 6000,
        theme: {
            background: "#0f1217",
            foreground: "#e6edf3",
            cursor: "#e6edf3",
            cursorAccent: "#0f1217",
            selectionBackground: "rgba(122, 162, 247, 0.22)",
            black: "#151922",
            red: "#f7768e",
            green: "#9ece6a",
            yellow: "#e0af68",
            blue: "#7aa2f7",
            magenta: "#bb9af7",
            cyan: "#7dcfff",
            white: "#c0caf5",
            brightBlack: "#5b6370",
            brightRed: "#ff899d",
            brightGreen: "#b7e27d",
            brightYellow: "#f3c980",
            brightBlue: "#8db3ff",
            brightMagenta: "#ceb7ff",
            brightCyan: "#97e6ff",
            brightWhite: "#ffffff",
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
            case "setTitle":
                setTitle(message.title);
                break;
            default:
                break;
        }
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

    window.addEventListener("resize", fitTerminal);

    requestAnimationFrame(() => {
        fitTerminal();
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

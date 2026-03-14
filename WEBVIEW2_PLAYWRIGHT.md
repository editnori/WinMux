# WebView2 + Playwright

This repo can expose the embedded terminal renderer inside the native WinUI app over the Chrome DevTools Protocol.

What this gives you:

- inspect native shell state through the paired automation endpoint
- inspect the WinUI visual tree and interactive native controls
- inspect resolved layout/style metadata for native controls
- trigger thread, tab, and theme actions without manual clicking
- target native controls by `automationId`, `elementId`, `name`, or annotated `refLabel`
- inspect external desktop windows and drive them with Win32 input injection
- inspect and act on external desktop windows semantically through the UIA helper
- capture multi-frame native render traces after actions
- capture native frame recordings with manifest output and optional mp4 encoding
- inspect native event logs for tab/thread/render sequencing
- capture native window screenshots from the running WinUI app
- capture annotated native screenshots with overlay labels
- inspect terminal scrollback, visible rows, cursor, selection, and session metadata
- inspect the real `WebView2` surface that the native app is using
- attach Playwright to the running native app renderer
- reload renderer HTML, CSS, and JS from the repo `Web/` folder without rebuilding native code

What this does not give you:

- direct inspection of non-WebView WinUI controls

The repo uses Bun for package management and script entrypoints.

The repo-local Bun scripts intentionally run the Playwright helpers through Windows `node`, not WSL `node`, because the WebView2 CDP port is Windows-local and Windows Node can reach it reliably.

## Start the native app in debug mode

```powershell
bun run webview2:start
```

This does four things:

1. Builds the WinUI app.
2. Launches it with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222 --remote-debugging-address=0.0.0.0`.
3. Points the embedded renderer at the repo `Web/` folder through `NATIVE_TERMINAL_WEB_ROOT`.
4. Starts the native automation endpoint on port `9331`.

The launcher also exposes the debug port on an address that WSL can reach, so the local Playwright install inside this repo can attach from `js_repl` or from the Node helpers.

You can change the port:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-webview2-debug.ps1 -Port 9333
```

## Inspect available targets

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
bun run native:desktop-windows
bun run native:desktop-uia-tree
bun run native:events
bun run native:recording-status
bun run native:desktop-action -- '{"action":"focusWindow","titleContains":"WinMux"}'
bun run native:desktop-uia-action -- '{"action":"focus","titleContains":"WinMux","name":"WinMux"}'
bun run native:terminal-state
bun run native:recording-start -- '{"fps":24,"maxDurationMs":5000,"keepFrames":false}'
bun run native:recording-stop
bun run native:demo-recording
bun run native:render-trace
bun run native:smoke
bun run webview2:targets
```

## Drive the native shell

```bash
bun run native:action -- '{"action":"newThread"}'
bun run native:action -- '{"action":"newTab"}'
bun run native:action -- '{"action":"moveTabAfter","tabId":"...","targetTabId":"..."}'
bun run native:action -- '{"action":"setTheme","value":"light"}'
bun run native:ui-action -- '{"action":"click","refLabel":"e2"}'
bun run native:ui-action -- '{"action":"click","automationId":"shell-nav-settings"}'
bun run native:screenshot
bun run native:screenshot:annotated
```

## Capture the current renderer

```bash
bun run webview2:screenshot
```

Or write to a specific file:

```bash
node ./tools/webview2-screenshot.mjs ./tmp/native-shot.png
```

## Evaluate live page state

```bash
bun run webview2:eval -- "document.title"
```

Or inspect something deeper:

```bash
powershell.exe -NoProfile -Command "node .\tools\webview2-eval.mjs \"document.body.innerText\""
```

## `js_repl` attach flow

Once the app is running in debug mode, use the same pattern as the Playwright skill, but attach over CDP instead of launching Chromium:

```javascript
var chromium;
var browser;
var context;
var page;
var fs;

({ chromium } = await import("playwright"));
fs = await import("node:fs/promises");
const resolv = await fs.readFile("/etc/resolv.conf", "utf8");
const nameserver = resolv.split(/\r?\n/).find((line) => line.startsWith("nameserver "))?.split(/\s+/)[1];
browser = await chromium.connectOverCDP(`http://${nameserver ?? "127.0.0.1"}:9222`);
context = browser.contexts()[0];
page = context.pages().find((p) => p.url().includes("terminal-host.html")) ?? context.pages()[0];

console.log(await page.title());
console.log(page.url());
```

Renderer-only iteration loop:

1. Edit files under `Web/`.
2. Reload the attached page:

```javascript
await page.reload({ waitUntil: "domcontentloaded" });
```

Native shell or startup changes still require relaunching the app.

The native automation loop covers the shell-level gaps that CDP cannot:

- shell state inspection
- native control discovery and generic UI actions
- desktop window enumeration and Win32 pointer/keyboard control
- external semantic UIA inspection and actions through `scripts/run-desktop-uia.ps1`
- native frame recording
- render trace capture
- native event logs
- thread and tab actions
- theme changes
- terminal inspection
- native window screenshots

## Setup

Install the repo-local tooling with Bun:

```bash
bun install
```

## Why this works

Microsoft documents WebView2 debugging via additional browser arguments and specifically calls out `--remote-debugging-port=9222` as the way to expose the DevTools endpoint for a running WebView2 app. Playwright can then attach to that endpoint with `connectOverCDP`.

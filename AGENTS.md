# AGENTS.md

## Purpose

This repo is a native Windows terminal shell built with WinUI 3.

The current shape of the app is:

- `net8.0-windows10.0.19041.0`
- Windows App SDK `1.8`
- native WinUI chrome
- ConPTY-backed shell process host
- `WebView2` terminal renderer using local HTML/CSS/JS
- Bun-managed debug helpers for attaching Playwright to the embedded renderer
- native automation endpoints for shell state, UI-tree inspection, style metadata, generic UI actions, desktop window automation, render tracing, event logging, terminal inspection, and screenshots
- native frame recording and external semantic UIA helpers for OS-owned windows

This file is the handoff document for future agents.

## Current product state

The app is no longer the original sample shell.

What exists now:

- fixed native shell layout with a dense inline sidebar and a top pane strip
- multiple projects, each with nested threads and per-thread pane workspaces
- worktree-aware thread metadata and a collapsible right-side inspector rail for the active thread
- autosaved workspace/session replay across app relaunches
- terminal panes backed by `TerminalControl`
- browser preview panes backed by `WebView2` with a built-in start page and a shared WinMux browser profile
- imported browser-password CSV support backed by a WinMux-encrypted credential store
- Preferences management for the WinMux credential vault: import, per-site delete, clear, and manual autofill
- editor panes backed by `TerminalControl` launching `nvim .`
- ConPTY process bridge in C#
- shared renderer under `Web/` hosted inside `WebView2`
- WebView2 CDP debug workflow for Playwright-style inspection
- native automation loop for shell state, UI-tree snapshots, style metadata, generic UI actions, desktop window automation, render traces, terminal snapshots, event logs, and native window screenshots
- native frame recording and script-backed semantic UIA control for external windows
- async git refresh plus render-key coalescing on the shell so thread switches and inspector refreshes do less work on the UI path

What does not exist yet:

- thread overview / niri-style vertical workspace navigation
- true ConPTY process hibernation beyond workspace replay
- thread-start / checkpoint diff history beyond the current live git snapshot
- true live Chrome-profile reuse and Google account sync parity inside the shared browser
- custom pane-strip visuals and overview polish

## Important files

### Native shell

- [MainPage.xaml](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml)
- [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs)
- [SettingsPage.xaml](/mnt/c/Users/lqassem/native-terminal-starter/SettingsPage.xaml)
- [Styles.xaml](/mnt/c/Users/lqassem/native-terminal-starter/Styles.xaml)
- [Shell/WorkspaceModels.cs](/mnt/c/Users/lqassem/native-terminal-starter/Shell/WorkspaceModels.cs)

### Terminal host

- [Terminal/ConPtyConnection.cs](/mnt/c/Users/lqassem/native-terminal-starter/Terminal/ConPtyConnection.cs)
- [Terminal/TerminalControl.xaml](/mnt/c/Users/lqassem/native-terminal-starter/Terminal/TerminalControl.xaml)
- [Terminal/TerminalControl.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/Terminal/TerminalControl.xaml.cs)

### Additional panes

- [Panes/BrowserPaneControl.xaml](/mnt/c/Users/lqassem/native-terminal-starter/Panes/BrowserPaneControl.xaml)
- [Panes/BrowserPaneControl.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/Panes/BrowserPaneControl.xaml.cs)

### Native automation

- [Automation/NativeAutomationContracts.cs](/mnt/c/Users/lqassem/native-terminal-starter/Automation/NativeAutomationContracts.cs)
- [Automation/NativeAutomationServer.cs](/mnt/c/Users/lqassem/native-terminal-starter/Automation/NativeAutomationServer.cs)
- [Automation/NativeDesktopAutomation.cs](/mnt/c/Users/lqassem/native-terminal-starter/Automation/NativeDesktopAutomation.cs)
- [Automation/NativeWindowRecorder.cs](/mnt/c/Users/lqassem/native-terminal-starter/Automation/NativeWindowRecorder.cs)
- [MainWindow.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainWindow.xaml.cs)
- [AUTOMATION_REFERENCE.md](/mnt/c/Users/lqassem/native-terminal-starter/AUTOMATION_REFERENCE.md)

### Shared renderer

- [Web/terminal-host.html](/mnt/c/Users/lqassem/native-terminal-starter/Web/terminal-host.html)
- [Web/terminal-host.css](/mnt/c/Users/lqassem/native-terminal-starter/Web/terminal-host.css)
- [Web/terminal-host.js](/mnt/c/Users/lqassem/native-terminal-starter/Web/terminal-host.js)
- [Web/vendor/xterm.min.js](/mnt/c/Users/lqassem/native-terminal-starter/Web/vendor/xterm.min.js)
- [Web/vendor/xterm.css](/mnt/c/Users/lqassem/native-terminal-starter/Web/vendor/xterm.css)
- [Web/vendor/xterm-addon-fit.min.js](/mnt/c/Users/lqassem/native-terminal-starter/Web/vendor/xterm-addon-fit.min.js)

### Debug workflow

- [package.json](/mnt/c/Users/lqassem/native-terminal-starter/package.json)
- [bun.lock](/mnt/c/Users/lqassem/native-terminal-starter/bun.lock)
- [scripts/start-webview2-debug.ps1](/mnt/c/Users/lqassem/native-terminal-starter/scripts/start-webview2-debug.ps1)
- [scripts/run-native-automation.ps1](/mnt/c/Users/lqassem/native-terminal-starter/scripts/run-native-automation.ps1)
- [scripts/run-terminal-browser-smoke.ps1](/mnt/c/Users/lqassem/native-terminal-starter/scripts/run-terminal-browser-smoke.ps1)
- [scripts/run-desktop-uia.ps1](/mnt/c/Users/lqassem/native-terminal-starter/scripts/run-desktop-uia.ps1)
- [tools/webview2-debug-utils.mjs](/mnt/c/Users/lqassem/native-terminal-starter/tools/webview2-debug-utils.mjs)
- [tools/webview2-targets.mjs](/mnt/c/Users/lqassem/native-terminal-starter/tools/webview2-targets.mjs)
- [tools/webview2-screenshot.mjs](/mnt/c/Users/lqassem/native-terminal-starter/tools/webview2-screenshot.mjs)
- [tools/webview2-eval.mjs](/mnt/c/Users/lqassem/native-terminal-starter/tools/webview2-eval.mjs)
- [tools/winmux_browser_bridge.py](/mnt/c/Users/lqassem/native-terminal-starter/tools/winmux_browser_bridge.py)
- [WEBVIEW2_PLAYWRIGHT.md](/mnt/c/Users/lqassem/native-terminal-starter/WEBVIEW2_PLAYWRIGHT.md)
- [FOUNDATION_TODO.md](/mnt/c/Users/lqassem/native-terminal-starter/FOUNDATION_TODO.md)

### Backlog / direction

- [ARCHITECTURE.md](/mnt/c/Users/lqassem/native-terminal-starter/ARCHITECTURE.md)
- [ROADMAP.md](/mnt/c/Users/lqassem/native-terminal-starter/ROADMAP.md)
- [UI_TODO.md](/mnt/c/Users/lqassem/native-terminal-starter/UI_TODO.md)

## How the app works

### Shell

`MainPage` owns the outer shell.

Responsibilities:

- collapsible inline sidebar via `SplitView`
- project -> thread -> pane workspace model with per-project root paths and shell profiles
- top-level `TabView`
- settings view switching
- creating, selecting, and closing pane workspaces
- focusing the selected pane host

### Terminal process model

`ConPtyConnection` launches the shell through Windows pseudo console APIs.

Responsibilities:

- create pseudo console
- create the child shell process
- forward output back to the UI
- write terminal input to the shell
- resize the pseudo console
- own child process lifetime
- expose browser-automation bridge environment variables into Windows shells and WSL shells

### Terminal UI model

`TerminalControl` is not a hand-drawn terminal anymore.

It hosts `WebView2`, loads the shared renderer, and bridges messages:

- native -> web: output, title, focus, system messages
- web -> native: input, resize, title updates, focus requests

It also supports a debug override:

- `NATIVE_TERMINAL_WEB_ROOT`

If that env var is set, the control loads `terminal-host.html` directly from the repo `Web/` folder instead of the copied build output.

### Shared renderer model

The `Web/` folder is the real terminal renderer.

It uses xterm.js and can run in two modes:

- real mode inside `WebView2`
- standalone mock mode in a browser when no `window.chrome.webview` bridge exists

This is intentional: it makes renderer iteration and visual QA much faster.

## Package manager

Use Bun, not npm.

Commands:

```bash
bun install
bun run webview2:start
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
bun run native:desktop-windows
bun run native:desktop-uia-tree
bun run native:events
bun run native:events:clear
bun run native:recording-status
bun run native:ui-action -- '{"action":"click","refLabel":"e2"}'
bun run native:desktop-action -- '{"action":"focusWindow","titleContains":"WinMux"}'
bun run native:desktop-uia-action -- '{"action":"focus","titleContains":"WinMux","name":"WinMux"}'
bun run native:terminal-state
bun run native:browser-state
bun run native:browser-eval -- "<pane-id>" "document.title"
bun run native:browser-screenshot -- "<pane-id>"
bun run native:agent-browser-smoke
bun run native:render-trace
bun run native:recording-start -- '{"fps":24,"maxDurationMs":5000,"keepFrames":false}'
bun run native:recording-stop
bun run native:demo-recording
bun run native:demo-recording:cinematic
bun run native:new-project-recording
bun run native:action -- '{"action":"newThread"}'
bun run native:action -- '{"action":"moveTabAfter","tabId":"...","targetTabId":"..."}'
bun run native:screenshot
bun run native:screenshot:annotated
bun run native:smoke
bun run webview2:targets
bun run webview2:screenshot
bun run webview2:eval -- "document.title"
```

Note:

- Bun is the repo package manager and script entrypoint.
- The debug scripts still invoke Windows `node` inside PowerShell because the `WebView2` CDP port is Windows-local and this path is the reliable one from WSL.

## Build and run

Native build:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\SelfContainedDeployment.csproj -p:Platform=x64
```

Native app launch:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\SelfContainedDeployment.csproj -p:Platform=x64
```

## WebView2 debug workflow

This is the current equivalent to a Playwright/Electron-style renderer loop.

1. Run:

```bash
bun run webview2:start
```

2. This builds and launches the app with:

- `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222 --remote-debugging-address=0.0.0.0`
- `NATIVE_TERMINAL_WEB_ROOT=<repo>/Web`

3. Attach helpers:

```bash
bun run native:health
bun run native:state
bun run native:browser-state
bun run webview2:targets
bun run webview2:screenshot
bun run webview2:eval -- "document.title"
```

What this is good for:

- inspecting shell state without clicking through it manually
- dumping the WinUI visual tree and interactive controls
- reading resolved colors, padding, margins, and sizing from the same tree
- targeting native controls by `automationId`, `elementId`, `name`, or annotated `refLabel`
- controlling external OS windows through desktop window enumeration and Win32 input injection
- semantically inspecting and acting on external OS windows through `scripts/run-desktop-uia.ps1`
- tracing multiple native render frames after an action
- recording native frame sequences with manifest output and optional mp4 encoding
- controlling native window state directly with move, center, resize, maximize, and topmost actions
- reading event logs for tab/thread/render sequencing
- creating threads and tabs from the outside
- switching theme and capturing native window screenshots
- taking annotated native screenshots with overlay refs
- inspecting terminal scrollback, visible rows, selection, cursor, and shell metadata
- inspecting the real renderer inside the native app
- inspecting browser panes through native browser-state, browser-eval, and browser-screenshot routes
- smoke-testing terminal-side WSL agents against the shared browser automation bridge
- CSS and JS debugging
- capturing screenshots
- reading live DOM state

What it is not good for:

- debugging layout of non-WebView controls

If the built-in control layer ever stops being enough, add Windows-native UI automation as a fallback instead of replacing the internal hooks.

## UI direction

Current UI direction is intentionally subtractive:

- no floating sidebars
- no nested rounded cards everywhere
- no decorative headers unless they earn their space
- flat surfaces
- tighter tab/shell chrome
- terminal area gets priority over furniture

If you are changing the shell, read:

- [UI_TODO.md](/mnt/c/Users/lqassem/native-terminal-starter/UI_TODO.md)

The biggest remaining visual gap is the stock `TabView`. It is the heaviest remaining piece of chrome.

## Known limitations

- The pane strip is still mostly stock WinUI.
- The native title bar and shell chrome are not yet unified into a tighter single system.
- Theme propagation is simple and not deeply modeled yet.
- The WebView2 debug helpers currently depend on the app being launched through the provided PowerShell script.
- Browser panes use one shared WinMux WebView2 profile. That improves persistence, but it still is not the same thing as reusing the live Chrome profile or full Google Sync.

## Guardrails for future agents

- Do not reintroduce the old sample navigation pattern.
- Do not bring back the dead `TerminalBuffer` / `VtParser` path unless there is a deliberate reason to replace xterm.js.
- Keep terminal renderer work inside `Web/` when possible.
- Keep native shell work in `MainPage.*`, `SettingsPage.*`, and `Styles.xaml`.
- Prefer space efficiency over decorative chrome.
- If you need browser-style renderer debugging, use the existing WebView2 flow before inventing a new one.
- Prefer the built-in native automation routes before reaching for external UI automation.
- Prefer the shared browser profile and the terminal browser bridge over introducing another legacy browser-profile mode.

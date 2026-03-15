# WinMux

WinMux is a native Windows terminal-first workspace shell built with WinUI 3, Windows App SDK, ConPTY, and WebView2.

The app is no longer a starter shell. The current repo includes:

- multi-project workspaces with nested threads
- per-thread pane workspaces with terminal, browser, editor, and diff panes
- worktree-aware thread metadata and a collapsible right-side inspector rail
- autosaved workspace/session restore across relaunch
- replay-command capture for Codex and Claude terminal sessions
- a shared WinMux browser profile plus an encrypted WinMux credential vault
- native automation routes for shell state, UI trees, UI actions, browser state/eval/screenshot, terminal inspection, events, screenshots, render traces, and recording

## Build

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\SelfContainedDeployment.csproj -p:Platform=x64
```

## Run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\SelfContainedDeployment.csproj -p:Platform=x64
```

## Debug And Automation

Install repo-local tooling with Bun:

```bash
bun install
```

Useful entrypoints:

```bash
bun run webview2:start
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:terminal-state
bun run native:browser-state
bun run native:smoke
```

## Key Docs

- `AGENTS.md`: current product shape and guardrails
- `AUTOMATION_REFERENCE.md`: native automation surface
- `WEBVIEW2_PLAYWRIGHT.md`: WebView2 debug workflow
- `ASKS_AUDIT.md`: current ask-by-ask audit and prioritized next pass

# WinMux

WinMux is a native Windows workspace shell built with WinUI 3, ConPTY, and WebView2.

It combines terminals, browser panes, editor panes, patch review, worktree-aware threads, session restore, and a full native automation surface that can be driven from Bun. The repo is set up so a human can use the shell directly while an LLM can inspect, drive, screenshot, and record the exact same native app.

## What WinMux does

- Native WinUI 3 shell with a dense multi-pane workspace.
- Project and thread model with worktree-aware terminals and overflow-thread behavior when a pane set is already full.
- Terminal, browser, editor, and diff panes in the same workspace.
- Patch review with live/baseline/checkpoint sources and structured diff automation.
- Session persistence across app relaunches, including restored pane layouts and replay metadata.
- Native automation routes for shell state, UI tree, UI actions, browser state, terminal state, diff state, editor state, screenshots, recordings, render traces, events, desktop window control, and desktop UIA fallback.

For the fuller feature inventory, see [FEATURES.md](FEATURES.md).

## Why the automation matters

WinMux is not just scriptable around the edges.

The Bun wrappers in this repo let an agent inspect and control the real native app:

- `bun run native:state`
- `bun run native:ui-tree`
- `bun run native:terminal-state`
- `bun run native:browser-state`
- `bun run native:diff-state`
- `bun run native:editor-state`
- `bun run native:screenshot`
- `bun run native:recording-start`
- `bun run native:recording-stop`

That means an LLM can:

- read the live workspace state
- click native controls
- type into terminals
- inspect diff/editor/browser panes
- capture screenshots and recordings
- drive demos and regression flows end to end

## Run locally

```bash
bun install
bun run webview2:start
```

Once the app is running, the main automation helpers are:

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:screenshot
```

## Cinematic recordings

The repo now includes a recording suite intended for public demos and shareable walkthroughs.

The main entrypoint is:

```bash
bun run native:recording-suite
```

That suite defaults to cinematic settings and writes a manifest plus per-recording folders under:

```text
tmp/automation-captures/winmux-recording-suite-cinematic-<timestamp>/
```

The suite generates:

- `overview`: broad product walkthrough of the shell chrome, tabs, settings, and thread flows
- `workspace-showcase`: terminals, browser, editor, diff review, file browser, fit/lock/zoom, worktree-scoped terminals, and overflow-thread behavior
- `feature-tour`: project/thread/worktree/browser/review/settings walkthrough
- `patch-review`: focused review recording for the diff surface
- `new-project`: project creation and empty-state recovery
- `tab-switch`: fast pane switching and strip behavior
- `automation-tour`: Bun-driven native control from inside WinMux itself
- `session-restore`: save-state and restored-state clips showing session replay across relaunch

You can also run the focused recordings directly:

```bash
bun run native:demo-recording:cinematic
bun run native:feature-tour-recording
bun run native:workspace-showcase-recording
bun run native:patch-review-recording
bun run native:new-project-recording
bun run native:tab-switch-recording
bun run native:automation-tour-recording
bun run native:session-restore-recording
```

The public-facing recordings default to light mode and explicitly showcase both light and dark themes during the flows.

## Build an installable publish output

The project already has Windows publish profiles in `Properties/PublishProfiles/`.

For an x64 release publish:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish .\SelfContainedDeployment.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishProfile=Properties\PublishProfiles\win10-x64.pubxml
```

The publish output lands under the standard `bin/Release/.../publish/` path for the target runtime.

## Repo structure

- `MainPage.*`: native shell layout and workspace model
- `Terminal/`: ConPTY terminal host and WebView2 renderer bridge
- `Panes/`: browser, editor, and diff panes
- `Web/`: shared terminal/editor frontend assets
- `Automation/`: native automation server and recording support
- `scripts/`: Bun/PowerShell helpers for automation, demos, and recordings

## Notes

- The app name is WinMux, even though the historical project file is still `SelfContainedDeployment.csproj`.
- Some Windows packaging warnings can still appear on debug launches depending on the local environment, but the main automation and recording flows are designed around the unpackaged debug build.

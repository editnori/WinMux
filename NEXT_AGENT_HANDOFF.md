# Next Agent Handoff

This file is the compact catch-up for the next agent.

It supplements:

- `AGENTS.md`
- `ASKS_AUDIT.md`
- `FEATURES.md`
- `FOUNDATION_TODO.md`

This handoff focuses on:

- what the user actually asked for across the long thread
- what is already shipped
- what is still open
- what is currently uncommitted in the worktree

## What The User Wanted

### Core product direction

- A real multiplexor shell, not endless tiny tmux-style splits.
- Pane visibility capped around `2-4`, with overflow moving into another thread instead of shrinking forever.
- Threads and panes working together as one workspace model.
- More than terminals in panes: browser, editor, diff, and possibly richer surfaces later.
- Strong native automation, visual inspection, recording, and control of the app as it grows.
- No "reset by shutting down WSL" nonsense.

### Browser and credentials

- Browser panes usable for actual agent workflows, not just static preview.
- Shared browser identity model instead of per-project browser weirdness.
- Password import from Chrome/Google CSV.
- Encrypted WinMux-owned credential vault with save/delete/clear/manual autofill.
- Safer autofill behavior with practical matching and site-level actions.
- Better browser profile/password behavior overall.
- Eventually browser internal tabs.
- Longer-term: true Chrome/live profile parity if possible.

### Layout, resize, theme, and session continuity

- Better pane resizing and scaling behavior.
- More intuitive multi-axis resizing in 3/4-pane layouts.
- Fit/rebalance controls.
- Better light mode and fewer dark/light inconsistencies.
- Session persistence and useful session replay.
- Resume restoration for Codex/Claude style sessions.
- Less ugly chrome and less stock `TabView` feel.

### Git, diff, worktrees, and inspector

- Worktree-aware threads.
- Git state as source of truth, not terminal scraping.
- Right-side inspector with changed files and a real review pane.
- Patch review out of the sidebar and into the workspace.
- Better patch rendering, correct light-mode colors, and no stale diff state.
- Thread baseline/checkpoint review history.
- Eventually richer full-file review, not only patch text.

### Thread rail, performance, and UX polish

- Better thread/project actions.
- Clear-thread and clear-all behavior without crashes.
- Performance with many threads/projects.
- Cleaner top chrome.
- Keep UI subtractive, dense, and consistent.

### Later explicit asks in this session

- Full visual/control instrumentation of WinMux.
- Recordings and screenshots to review each pass.
- GUI editor pane instead of terminal editor as the main editor experience.
- Inspector `Files` tab like a VS Code-style directory, not a second editor header/sidebar.
- Simpler tab chrome: keep `Term / Web / Diff` feel, avoid elongated pills.
- Move thread summary info into the left rail, not clutter the top strip.
- Clean temp/demo projects from the normal session.

## What Landed

### Workspace and shell

- Real project -> thread -> pane workspace model.
- Pane-count limits with overflow-thread behavior.
- Terminal, browser, diff, and GUI editor panes.
- Worktree-aware threads.
- Session persistence and replay metadata.
- First-pass thread overview exists.
- Better pane resize tools: fit panes, rebalance, shift-drag dual-axis behavior.

### Browser

- Shared WinMux browser profile.
- Browser panes with internal browser tabs.
- Browser automation routes.
- Password CSV import.
- Encrypted WinMux credential vault.
- Manual/context autofill flows.
- Safer origin/host credential matching and signup/reset autofill blocking.

### Git and review

- Right inspector rail.
- Git diff based on repo state.
- Diff pane in workspace.
- Baseline + manual checkpoint review source model.
- Multi-file combined patch review.
- Structured `/diff-state` automation.
- Light-mode diff rendering.

### Automation and QA

- Native automation for state, UI tree, UI actions, screenshots, recordings, render traces, terminal/browser state, diff state.
- Recorded feature tours and patch review captures.
- Terminal/browser bridge for WSL agents.

### Editor

- GUI editor pane is now Monaco-backed in WebView2.
- File browser moved to inspector `Files` tab.
- File selection persists and editor state is automatable.

## What Is Still Open

### Highest-value product gaps

- Full-file or side-by-side review surface.
- True Chrome/live-profile/sync parity.
- Larger second-pass overview/pane-strip polish.
- Large-workspace performance pass with evidence.
- Broader terminal hardening beyond the recent startup fixes.

### Current UX gaps the user still cares about

- Files inspector should feel closer to a real code explorer.
- Review file switching must never regress into `Patch unavailable` for non-first files.
- Inspector/sidebar chrome should stay compact and not waste width.
- Browser/tab compactness and pane-fit behavior still need ongoing visual tuning.

## Current Uncommitted Worktree State

These changes are in the repo right now and are not committed yet.

### New editor/files architecture

- `Panes/EditorPaneControl.xaml`
- `Panes/EditorPaneControl.xaml.cs`
- `Web/editor-host.html`
- `Web/editor-host.css`
- `Web/editor-host.js`
- `Web/vendor/monaco/`

### Updated shell/diff/session plumbing

- `MainPage.xaml`
- `MainPage.xaml.cs`
- `Panes/DiffPaneControl.cs`
- `Automation/NativeAutomationContracts.cs`
- `Automation/NativeAutomationServer.cs`
- `MainWindow.xaml.cs`
- `Persistence/WorkspaceSessionStore.cs`
- `Shell/WorkspaceModels.cs`
- `scripts/run-native-automation.ps1`
- `package.json`
- `bun.lock`
- `FEATURES.md`
- `AGENTS.md`

### Dirty but user-owned / do not casually overwrite

- `README.md`

## Most Recent Fixes In Progress

These are the newest changes the next agent must understand first.

- Files inspector crash fixed by removing the `TreeView.ItemTemplate` cast path and building directory rows manually in code.
- Diff-pane light-mode header brush resolution fixed.
- `/diff-state` raw text now aggregates full multi-file patch text.
- Diff selection flow hardened so multi-file review should not swap into partial `Patch unavailable` state when selecting the second or third changed file.
- Generic `selectDiffFile` automation now routes through the safer review-selection path.
- Files inspector row styling tightened to use smaller badges and denser text.

## Known Validation State

- `bin/automation44/` built successfully.
- `bin/automation43/` fixed the Files-sidebar startup crash.
- Latest normal launched build in-session was `automation44`.
- Recent startup check after the crash fix reported `NO_STARTUP_ERROR`.

One validation gap still remains:

- The final automated sidecar check for second/third diff-file switching was flaky because the isolated harness kept colliding with normal session behavior.
- The code path is patched, but the next agent should manually verify the exact user repro in the live app before claiming it is fully closed.

## Immediate Next TODO

1. Manually verify multi-file review switching in the live app:
   `README.md` -> second changed file -> third changed file.
   Confirm no `Patch unavailable` fallback appears.

2. Continue explorer polish:
   smaller badges, better file-type presentation, fuller-width rows, better changed-file emphasis.

3. Decide whether to keep evolving the current unified patch review or start the fuller full-file review surface.

4. Keep browser compact-mode polish going only after the review/files path is stable.

5. Commit the current Monaco/files/diff work once the live manual diff-switch regression is confirmed.

## Useful Commands

Build:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\SelfContainedDeployment.csproj -p:Platform=x64 -p:OutDir=bin\automation44\
```

Run:

```powershell
& "C:\users\lqassem\native-terminal-starter\bin\automation44\SelfContainedDeployment.exe"
```

## Advice For The Next Agent

- Start from the live user repro, not from docs.
- Do not trust old session state; the user has repeatedly reset/cleaned it.
- Do not regress the subtractive UI direction by adding more labels, pills, or card chrome.
- Do not overwrite `README.md` unless the user explicitly wants that file touched.
- Before changing diff review again, test second-file and third-file selection directly.

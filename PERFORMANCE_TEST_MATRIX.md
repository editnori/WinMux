# WinMux Performance Test Matrix

This document maps the current WinMux feature surface to the performance coverage that exists in this repo today.

It is intentionally pragmatic:

- use the perf governor for features that already have repeatable action runners
- use smoke and stress scripts for heavier multi-surface flows
- use recording scripts for fixture-heavy walkthroughs that are hard to benchmark in isolation
- call out the remaining manual-only areas instead of pretending they are covered

## Current coverage snapshot

Verified against the current repo tooling:

- `perf/feature-catalog.json` now tracks `67` performance feature entries
- `bun run native:perf:list` reports:
  - `49` automated benchmark entries
  - `18` fixture-required entries
  - `0` manual-only entries
- `bun run native:perf:check` still reports full action coverage:
  - `60` `/action` values handled by `MainPage.PerformAutomationAction(...)`
  - `60` of `60` mapped into the performance catalog

Targeted benchmark runs executed in this workspace after the catalog expansion:

- passing targeted slices:
  - `browser.credentials.import`
  - `notes.add-thread-note`
  - `terminal.input`
  - `shell.thread-worktree`
  - `diff.capture-checkpoint`
  - `diff.select-review-source`
- current targeted budget miss:
  - `browser.start-page-theme-refresh`: `77.2ms` first render against a `75ms` budget
- broader full-suite rerun is still worth doing once the heavier baseline-restore path is trimmed for large mutating batches

Important detail:

- the perf catalog is action-complete, but not product-complete
- several non-`/action` capabilities still live outside the governor and are covered by smoke, stress, recording, or manual workflows instead
- action aliases share perf entries where they hit the same underlying implementation path

## Core commands

Use these as the main entrypoints:

```bash
bun run native:perf:list
bun run native:perf:check
bun run native:perf:run -- --include-mutating
bun run native:smoke
bun run native:stress
bun run native:agent-browser-smoke
```

Useful diagnostics for follow-up:

```bash
bun run native:perf-snapshot
bun run native:doctor
bun run native:render-trace
bun run native:recording-start -- '{"fps":24,"maxDurationMs":5000,"keepFrames":false}'
bun run native:recording-stop
```

## Automated benchmark coverage

These feature families already have repeatable runners in `perf/feature-catalog.json` and are exercised by:

```bash
bun run native:perf:run -- --include-mutating
```

### Shell and layout

- `shell.toggle-pane`: sidebar open/close response and first render
- `shell.inspector-toggle`: inspector rail toggle response and first render
- `shell.show-settings`: switch from terminal workspace to settings surface
- `shell.show-terminal`: return from settings to the terminal workspace
- `shell.project-switch`: switch between projects with async refresh budget
- `shell.thread-switch`: switch between threads with async refresh budget
- `shell.thread-worktree`: switch the active thread between restorable worktree roots
- `shell.theme`: action-driven theme switch timing
- `shell.profile`: action-driven shell-profile switch timing
- `shell.layout-dual`: set dual layout
- `shell.layout-quad`: set quad layout
- `shell.pane-split`: apply split ratios
- `shell.fit-panes`: fit panes
- `shell.fit-visible-panes`: fit visible panes
- `shell.fit-panes-lock`: toggle fit lock
- `shell.fit-visible-panes-lock`: toggle visible-pane fit lock

### Pane creation and review

- `pane.new-terminal-tab`: create terminal pane/tab
- `pane.new-browser-pane`: create browser pane
- `pane.new-editor-pane`: create editor pane
- `pane.zoom`: zoom/unzoom the active pane
- `pane.focus`: focus a pane through the automation action path
- `pane.select-tab`: change active pane selection
- `pane.close-tab`: close a pane tab
- `pane.rename`: rename a pane tab
- `diff.select-file`: open the selected diff file into the review surface
- `diff.capture-checkpoint`: capture a review checkpoint
- `diff.select-review-source`: switch between live and baseline review sources

### Persistence

- `session.autosave-write`: autosave timing sampled from `perf-snapshot`
- `session.save`: manual session-save action timing

### Notes, browser, and terminal

- `notes.add-thread-note`: create a thread note
- `notes.update-thread-note`: update an existing thread note
- `notes.archive-thread-note`: archive a note
- `notes.restore-thread-note`: restore an archived note
- `notes.select-thread-note`: change note selection
- `notes.open-thread`: open thread-scoped notes in the inspector
- `notes.open-project`: open project-scoped notes in the inspector
- `browser.navigate`: navigate the selected browser pane
- `browser.new-tab`: create an in-pane browser tab
- `browser.select-tab`: select an in-pane browser tab
- `browser.close-tab`: close an in-pane browser tab
- `browser.start-page-theme-refresh`: measure the in-place start-page theme refresh path
- `terminal.input`: measure terminal input-to-visible-text latency

## Fixture-required performance coverage

These are cataloged in `perf/feature-catalog.json`, but they still need a prepared workspace, a richer fixture, or a purpose-built runner to measure them consistently.

### Workspace lifecycle

- `workspace.new-project`
  - Current perf path: `bun run native:smoke`, `bun run native:new-project-recording`, `bun run native:stress`
- `workspace.new-thread`
  - Current perf path: `bun run native:smoke`, `bun run native:feature-tour-recording`
- `workspace.rename-thread`
  - Current perf path: `bun run native:smoke`
- `workspace.duplicate-thread`
  - Current perf path: `bun run native:smoke`, `bun run native:feature-tour-recording`
- `workspace.delete-thread`
  - Current perf path: `bun run native:smoke`
- `workspace.clear-project-threads`
  - Current perf path: `bun run native:smoke`
- `workspace.delete-project`
  - Current perf path: fixture/manual

### Pane interaction

- `pane.move-tab-after`
  - Current perf path: `bun run native:smoke`, `bun run native:tab-switch-recording`

### Diff and review

- `diff.refresh`
  - Current perf path: `bun run native:smoke`, `bun run native:stress`
- `diff.full-patch-review`
  - Current perf path: `bun run native:patch-review-recording`, `bun run native:feature-tour-recording`

### Browser pane flows

- `browser.credentials.import`
  - Current path: dedicated governor runner with disposable CSV fixtures
- `browser.credentials.delete`
  - Current path: dedicated governor runner with disposable imported credentials
- `browser.credentials.clear`
  - Current path: dedicated runner, but intentionally gated behind `WINMUX_PERF_ALLOW_VAULT_CLEAR=1`
- `browser.credentials.autofill-success`
- `browser.credentials.autofill-blocked`
- `browser.credentials.autofill-failed`
  - Current path: dedicated governor runners, but they still depend on a matching credential and stable browser fixture state

### Session recovery

- `session.restore`
  - Current perf path: `bun run native:session-restore-recording`
- `session.replay-restore`
  - Current perf path: `bun run native:session-restore-recording`

## Additional implemented features outside the current perf catalog

These are real shipped capabilities, but they are not currently represented as dedicated perf-governor feature ids.

### Native automation server and diagnostics

- Health, state, UI tree, desktop window enumeration, events, perf snapshot, and doctor routes
- UI-action path for semantic clicking/hovering/invoking controls
- Terminal/browser/diff/editor state snapshots
- Browser eval and browser screenshot routes
- Desktop-action routes
- Annotated screenshot, render trace, recording start/stop/status
- Current perf path:
  - cataloged governor entries now cover `ui-tree`, `browser-state`, `diff-state`, `editor-state`, `doctor`, `render-trace`, and `screenshot`
  - `bun run native:smoke`

### Terminal-to-browser bridge

- Shared browser bridge environment variables in terminal sessions
- WSL-safe PowerShell fallback path for bridge calls
- Claude and Codex terminal command detection in bridge smoke
- Current perf path:
  - `bun run native:agent-browser-smoke`

### Recording suite and demo scripts

- Overview/demo recording
- Workspace showcase recording
- Feature-tour recording
- Patch-review recording
- New-project recording
- Tab-switch recording
- Automation-tour recording
- Session-restore recording
- Current perf path:
  - Script-specific runtime and recording health
  - Native recording frame counts and manifest generation

### Browser profile management and start-page theming

- Shared browser profile seeding and repair
- Start-page rendering and in-place theme refresh
- Themed browser background and resize masking
- Current perf path:
  - `browser.start-page-theme-refresh`
  - `pane.new-browser-pane` plus `perf-snapshot` breakdowns (`browser.environment.get`, `browser.core.ensure`, `browser.configure`)
  - targeted render traces or preview captures when regressions appear

### Settings, notes CLI, and release packaging

- Settings screen for theme, default shell, pane limit, and browser vault management
- `bun run native:notes` CLI
- Installer build and tagged release packaging
- Current perf path:
  - theme/profile interaction timing is now covered through the shared action runners
  - release/build pipeline timing is still outside the governor

## Recommended per-feature test stack

Use this stack when touching a feature family:

- Shell/layout changes:
  - `bun run native:perf:run -- --include-mutating`
  - `bun run native:smoke`
- Terminal rendering or resize changes:
  - `bun run native:smoke`
  - `bun run native:agent-browser-smoke`
  - `bun run native:render-trace`
- Browser pane changes:
  - `bun run native:smoke`
  - `bun run native:workspace-showcase-recording`
  - `bun run native:browser-state`
- Diff/review changes:
  - `bun run native:smoke`
  - `bun run native:patch-review-recording`
- Session restore changes:
  - `bun run native:session-restore-recording`
  - `bun run native:perf:run -- --feature session.save`
- Notes/vault/settings changes:
  - `bun run native:perf:run -- --include-mutating --include-fixture-required --feature notes.add-thread-note,notes.update-thread-note,notes.archive-thread-note,notes.restore-thread-note,notes.select-thread-note,notes.open-thread,notes.open-project`
  - `bun run native:perf:run -- --include-mutating --include-fixture-required --feature browser.credentials.import,browser.credentials.delete,browser.credentials.autofill-success,browser.credentials.autofill-blocked,browser.credentials.autofill-failed`

## Gaps to automate next

If the goal is truly "performance coverage for every implemented feature," these are the next runners worth adding:

- session restore and replay restore budget assertions that emit pass/fail metrics instead of recording-only evidence
- browser credential clear on an isolated vault fixture so it can run ungated
- settings-surface-specific UI-action timings instead of only shared action timings
- browser profile seed/repair timings as dedicated feature ids rather than inferred breakdowns

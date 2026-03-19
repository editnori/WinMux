# WinMux Features

This is the code-grounded feature inventory for the native WinMux shell as it exists in this repo today.

It is split into three layers:

1. product capabilities that a user can see in the app
2. developer and automation surfaces that exist around the app
3. the complete `POST /action` vocabulary exposed by the native automation server

## Platform foundation

- Native WinUI 3 shell on `net8.0-windows10.0.19041.0` with Windows App SDK `1.8`
- Native WinUI window chrome with unpackaged debug runs and packaged installer builds
- ConPTY-backed shell hosting for terminal panes
- Native Windows Terminal-backed terminal panes plus WebView2-backed browser/editor experiences
- Bun-first tooling for native automation, renderer debugging, smoke tests, recordings, and perf checks

## Shell and workspace chrome

- Dense inline left sidebar for project and thread navigation
- Top pane strip for workspace tabs and pane actions
- Collapsible right inspector rail tied to the active thread
- Settings surface inside the shell instead of a separate external config flow
- Light, dark, and system theme support
- Smoother shell motion for sidebar toggles, inspector toggles, splitter drag preview, pane resize transitions, and theme changes
- Thread header and shell chrome that expose thread name, branch, and worktree context
- Active-project-only thread expansion in the sidebar so larger workspaces stay responsive
- Workspace overview cards and pane summaries for threads, including pane-kind badges and checkpoint/change metadata
- Overflow indicators when a thread owns more panes than the current layout can show at once

## Projects, threads, and pane orchestration

- Multiple projects, each with its own root path and shell profile
- Nested per-project threads with per-thread workspace state
- Create, select, rename, duplicate, delete, and clear threads
- Create and remove projects from the shell
- Empty-project recovery by creating a new thread when a project has none left
- Thread worktree overrides separate from the project root
- New sibling threads inherit the selected thread worktree when one is active
- Per-thread pane limits with overflow-thread behavior when a thread is already full
- Solo, dual, triple, and quad visible layouts
- Persisted primary and secondary split ratios per thread
- Splitter drag preview, center-splitter dual-axis resizing, Shift-resize support, and double-click rebalance
- Pane zoom/focus behavior inside a thread workspace
- Fit-visible-panes and auto-fit lock behavior for editor and diff surfaces
- Select, reorder, rename, focus, zoom, and close panes/tabs
- Persisted active-pane selection per thread

## Terminal surface

- ConPTY-backed shell sessions
- WSL, PowerShell, and Command Prompt shell profile support
- Default shell preference for newly created projects and tabs
- Per-pane terminal launch environment that reflects project/thread/worktree context
- Native Windows Terminal-derived renderer hosted through `EasyWindowsTerminalControl.WinUI` and `Microsoft.Terminal.WinUI3`
- Native terminal theming, focus, and sizing integrated into the WinUI shell
- HWND-hosted terminal surface with the usual airspace caveat for overlays and screenshot capture
- Terminal resize, focus, and automation state reporting
- Replay metadata capture for Codex and Claude-style resume flows
- Replay restore on session recovery when saved metadata can be turned back into a launch command
- Failed replay-restore panes remain visible as ended panes instead of silently disappearing on the next save
- Terminal-to-browser automation bridge environment variables injected into Windows shells and WSL shells

## Browser surface

- Browser panes as first-class workspace panes
- Shared WinMux-managed WebView2 browser profile across panes and projects
- Browser profile seeding and repair from detected Chromium data when available
- In-pane browser tab sessions with select/new/close behavior
- Browser address bar plus back, forward, home, refresh, and external-open controls
- Built-in browser start page that exposes local preview shortcuts and current project context
- Start page theme updates in place instead of full re-navigation on theme change
- Explicit themed browser background plus resize masking to reduce flash during resize/theme transitions
- Browser state, eval, and screenshot automation routes
- Browser preview capture support through WebView2
- Compact zoom handling for browser panes in denser layouts

## Browser credentials and vault

- WinMux credential vault with Windows-protected local storage
- Google Passwords CSV import into the WinMux vault
- Merge behavior for imported credentials instead of blind duplication
- Exact-origin and exact-host matching for autofill decisions
- Manual autofill from settings and from browser context menus
- Autofill suggestions limited to exact matches for the current page
- Safe-form blocking for signup/reset and multi-password pages
- Per-site delete and full-vault clear operations from settings
- Vault status surfaced both in settings and on the browser start page
- Native browser password autosave and general autofill intentionally disabled in favor of the WinMux vault
- Credential capture intentionally disabled inside WinMux-managed profiles

## Editor and diff review

- Monaco-backed editor panes hosted inside WebView2
- Inspector-side directory/file navigation for editor panes
- In-pane editing with save and reload flows
- Restored file selection for editor panes on session restore
- Diff panes that open from inspector file selection
- Unified diff rendering with metadata, hunk, addition, deletion, and context styling
- Theme-aware diff colors and line metadata
- Full patch review entrypoints from the shell
- Persisted selected diff path per thread
- Diff-pane restore when a thread returns with saved review state
- Structured diff-pane automation through `POST /diff-state`
- Fit-to-width behavior shared across editor and diff panes
- Diff host that can coordinate compare and patch panes under one surface

## Git and review state

- Worktree-aware thread metadata derived from git state instead of terminal parsing
- Active-thread git snapshot for branch, changed files, and selected diff metadata
- Inspector changed-file list with insertion/deletion counts
- Thread baseline capture for review
- Manual checkpoint capture with selectable checkpoint names
- Review-source switching between live, baseline, and checkpoint sources
- Review-source controls hidden until a thread actually has alternate review sources
- Persisted selected review source and selected checkpoint id per thread
- Baseline and checkpoint state persisted into session restore

## Inspector, notes, and preferences

- Collapsible inspector rail with thread-scoped repo context
- Thread and project notes inside the inspector
- Pane-attached notes
- Note creation, selection, update, deletion, archive, and restore flows
- Thread-scope and project-scope note views
- Archived note grouping with expandable archived sections
- Persisted selected note state
- Theme preference: light, dark, or system default
- Default shell preference: Command Prompt, PowerShell, or WSL
- Pane-limit preference for thread density
- Browser vault management from settings

## Persistence and recovery

- Workspace autosave across app restart
- Project, thread, pane, split ratio, selected tab, selected diff, selected note, review source, checkpoint, pane-limit, fit-lock, and zoom restore
- Session restore across app relaunch
- Replay-command persistence for terminal sessions
- Restore-time handling for missing project roots on a different machine
- Restore-time preservation of pane failures instead of silently dropping them

## Native automation, diagnostics, and tooling

- Native automation server with:
  - `GET /health`
  - `GET /state`
  - `GET /ui-tree`
  - `GET /desktop-windows`
  - `GET /recording-status`
  - `GET /events`
  - `GET /perf-snapshot`
  - `GET /doctor`
  - `POST /action`
  - `POST /ui-action`
  - `POST /terminal-state`
  - `POST /browser-state`
  - `POST /diff-state`
  - `POST /editor-state`
  - `POST /browser-eval`
  - `POST /browser-screenshot`
  - `POST /desktop-action`
  - `POST /render-trace`
  - `POST /recording/start`
  - `POST /recording/stop`
  - `POST /screenshot`
- Annotated native screenshots with ref labels
- Native frame recording and manifest output
- Render-trace capture for multiple frames after an action
- Event-log capture for shell/render sequencing
- Perf snapshot and doctor snapshots for UI responsiveness and timing
- Desktop window control and desktop UIA fallback tooling
- Bun wrappers for all major automation routes
- Notes CLI through `bun run native:notes`
- Scenario, benchmark, profile-action, and perf-governor tooling in `tools/native-debugger.mjs` and `tools/native-perf-governor.mjs`
- Native smoke, stress, terminal-browser smoke, and cinematic recording scripts

## Demo, recording, and release tooling

- Cinematic recording suite with overview, workspace showcase, feature tour, patch review, new project, tab switch, automation tour, and session restore flows
- Focused recording scripts for specific feature areas
- Browser/CDP debug workflow for the embedded WebView2 surfaces
- Installer build script that bundles a WebView2 bootstrapper when needed
- Tagged release workflow that publishes the Windows build and installer asset

## Complete native automation action vocabulary

These are the currently supported `request.action` values handled by `MainPage.PerformAutomationAction(...)`.

### Shell view and global state

- `togglePane`
- `showTerminal`
- `showSettings`
- `toggleInspector`
- `setTheme`
- `setProfile`
- `saveSession`

### Projects, threads, and workspace structure

- `newProject`
- `deleteProject`
- `newThread`
- `selectProject`
- `selectThread`
- `renameThread`
- `duplicateThread`
- `deleteThread`
- `clearProjectThreads`
- `setThreadWorktree`

### Pane and layout orchestration

- `newTab`
- `newBrowserPane`
- `newEditorPane`
- `selectTab`
- `moveTabAfter`
- `closeTab`
- `renamePane`
- `setLayout`
- `setPaneSplit`
- `fitPanes`
- `fitVisiblePanes`
- `toggleFitPanesLock`
- `toggleFitVisiblePanesLock`
- `togglePaneZoom`
- `focusPane`
- `input`

### Notes

- `setThreadNote`
- `addThreadNote`
- `createThreadNote`
- `updateThreadNote`
- `deleteThreadNote`
- `archiveThreadNote`
- `restoreThreadNote`
- `unarchiveThreadNote`
- `selectThreadNote`
- `editThreadNote`
- `showThreadNotes`
- `showProjectNotes`
- `showNotes`

### Git and review

- `refreshDiff`
- `captureCheckpoint`
- `openFullPatch`
- `openFullPatchReview`
- `selectReviewSource`
- `selectDiffFile`

### Browser-specific actions

- `navigateBrowser`
- `newBrowserTab`
- `selectBrowserTab`
- `closeBrowserTab`
- `importBrowserPasswordsCsv`
- `deleteBrowserCredential`
- `clearBrowserCredentials`
- `autofillBrowser`

## Known gaps

- No true ConPTY process hibernation beyond workspace replay and restore
- No live Chrome Sync or full live-Chrome-profile parity inside the shared browser profile
- No side-by-side or full-file diff surface yet; current review UI is a richer unified diff view
- Pane strip visuals and shell chrome are improved but still partially stock WinUI

# WinMux Features

This is the current feature inventory for the native WinMux shell as it exists in this repo today.

## Shell and workspace

- Native WinUI 3 shell on `net8.0-windows10.0.19041.0` with Windows App SDK `1.8`
- Project-based workspace model with nested threads
- Per-thread pane workspaces with pane-count limits and overflow-thread behavior
- New sibling threads inherit the selected thread's worktree path when one is active
- First-pass active-project thread overview inside the shell workspace
- Terminal, browser, editor, and diff panes
- Inline pane rename and thread rename flows
- Project removal from the project context menu
- Right-side inspector rail for active thread repo state
- Light and dark themes with native shell propagation
- Persisted split ratios and active-pane selection

## Terminal and editor

- ConPTY-backed shell sessions
- WSL, PowerShell, and Command Prompt shell profile support
- Per-pane terminal launch environment overrides for thread/project context
- WebView2-hosted terminal renderer using the shared `Web/terminal-host.*` frontend
- Hardened renderer startup handshake so fresh WSL tabs cannot miss the initial renderer `ready` signal
- Theme propagation into the terminal renderer
- Editor pane mode backed by terminal launch of `nvim .`
- Replay metadata capture for Codex and Claude resume commands
- Session restore that can replay stored resume commands on relaunch
- Failed restored replay panes stay in the workspace as ended tabs instead of disappearing on the next autosave

## Browser

- Shared WinMux WebView2 browser profile
- Browser panes as first-class workspace panes
- Lightweight in-pane browser tabs with selectable current page sessions
- Built-in browser start page
- Browser automation routes for state, eval, and screenshot
- Browser profile seeding/repair from the most relevant detected Chromium profile
- Preferred extension import path
- WinMux credential vault with Windows-protected storage
- Google Passwords CSV import with merge behavior
- Exact-origin and exact-host credential matching instead of broad subdomain suffix matching
- Manual autofill and context-menu autofill flows
- Signup/reset-safe credential capture and autofill blocking for unsafe multi-password forms

## Git and review

- Worktree-aware thread metadata
- Active-thread git snapshot from git state, not terminal parsing
- Thread baseline capture plus manual checkpoint snapshots for diff review
- Review-source switching between live state, thread baseline, and named checkpoints
- Inspector changed-file list with insertion/deletion counts
- Diff pane opens in review layout from inspector file selection
- Persisted selected diff path per thread
- Diff-pane restore when a thread has saved review state
- Structured diff-pane automation via `POST /diff-state`
- Unified review rows with line numbers, metadata/hunk/add/remove styling, and theme-aware colors
- Dedicated patch-review recording flow for review captures

## Automation and QA

- Native automation server with shell state, UI tree, UI actions, desktop actions, terminal state, browser state, diff state, screenshots, render traces, recordings, and event logs
- Terminal-state snapshots expose live/exited/startup/status metadata so smoke scripts can distinguish live shells from ended panes
- Annotated native screenshots with element refs
- Desktop window control and external UIA helper flows
- Bun wrappers for all core automation routes
- Native smoke script for shell regression coverage
- Terminal-to-browser bridge for WSL/agent workflows, including UTF-8-safe PowerShell bridge fallback from WSL
- Patch-review capture script with optional retained frame output

## Persistence and recovery

- Workspace session persistence across app restart
- Project/thread/pane restore on relaunch
- Selected thread, selected pane, split ratios, and selected diff restoration
- Replay-command persistence for terminal sessions

## Known gaps

- No side-by-side or full-file review surface yet; current review UI is a richer unified diff view
- No true Chrome Sync/live Chrome-profile parity
- No fully polished niri-style vertical workspace navigation yet; the current overview is a first pass
- Terminal startup still needs broader regression hardening even though the WebView2 init path has been tightened

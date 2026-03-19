# Changelog

## alpha-v0.1.6 - 2026-03-19

### Highlights

- Reworked the shell around split `MainPage` subsystems, staged git refresh, and lower-churn pane/layout updates so large workspaces switch threads and rebuild inspector state with less UI-path work.
- Promoted the native Windows Terminal host into the current baseline with safer HWND hosting, replay-restore fixes, hidden-pane suppression, stronger automation coverage, and improved focus/selection handoff.
- Rebuilt the inspector, browser, editor, and diff review surfaces with dedicated controls, grouped browser chrome, theme-aware Monaco styling, diff section navigation, and manual zoom behavior.
- Added theme mode plus palette packs (`Graphite`, `Harbor`, `Moss`, `Copper`) with settings UI, session replay persistence, and automation-visible theme metadata.

### Included commits

- `Fix replay restore regressions`
- `Polish shell motion and refresh README media`
- `Polish pane chrome and compact terminal prompt`
- `Snapshot native terminal baseline`
- `Trim snapshot churn and cache allocations`
- `Reduce UI churn and drop dead web terminal assets`
- `Extract shell UI models and trim stale roadmap`
- `Split MainPage rail and inspector subsystems`
- `Centralize shell theme fallbacks`
- `Extract MainPage git review subsystem`
- `Add engineering refactor backlog`
- `Decompose MainPage and extend perf diagnostics`
- `Refactor inspector surfaces and stage git refresh work`
- `Patch terminal automation crash path`
- `Hide terminal host when ancestor pane is collapsed`
- `Fix pane focus churn and settings visibility`
- `Polish theme packs and pane interaction flow`

### Detailed changelog

- Shell architecture and performance
  - Split `MainPage` into dedicated rail, notes, pane-workspace, session-restore, inspector-file, and git-review partials to make future shell work more localized.
  - Staged git refresh work, trimmed snapshot churn, and reduced cache/allocation overhead in the shell and editor paths.
  - Added refreshed engineering and performance backlog material to track the remaining refactor and regression work.
- Terminal host and automation
  - Checked in the native Windows Terminal baseline assets and wrapper work needed for the current `TerminalControl` host.
  - Hardened terminal hosting against automation crash paths and ancestor-collapse scenarios so hidden panes stop fighting layout and visibility.
  - Fixed replay-restore regressions, tightened pane focus churn, and improved terminal selection chrome during pane activation.
  - Extended automation/state contracts with explicit theme mode/theme pack metadata and current terminal-baseline coverage.
- Inspector, browser, editor, and diff review
  - Rebuilt inspector surfaces around dedicated file, notes, and review controls, then polished the file tree spacing, selection treatment, and staged refresh behavior.
  - Flattened the browser chrome into grouped controls, improved start-page theme synchronization, and added hosted-surface suppression plus manual zoom control during resize-heavy flows.
  - Updated the WebView editor host to apply shell-aware Monaco palettes, preserve side-by-side compare mode, and reduce review noise in line numbers, gutters, and selection effects.
  - Added previous/next change navigation, manual zoom, and tighter header chrome for diff review panes.
- Theming, docs, and release polish
  - Added persisted shell palette packs on top of the existing light/dark/system theme mode flow.
  - Expanded Settings so users can choose both theme mode and theme pack, with those values round-tripping through session restore.
  - Refreshed README media and checked in supplementary review/reference material under `docs - Copy/` for ongoing design and architecture work.

## alpha-v0.1.5 - 2026-03-18

### Highlights

- Stabilized shared browser behavior, tightened native automation helpers, and hardened the release/dev scripts around the current WinMux workspace model.

### Included commits

- `Stabilize WinMux browser and harden automation`

### Detailed changelog

- Added safer native automation access and local-dev helper scripts for automation, signing, and shortcuts.
- Tightened browser-pane state/profile handling, session restore plumbing, and terminal/browser bridge behavior around the shared WinMux browser profile.
- Expanded automation and perf catalog coverage so the repo reflects the current shell/runtime surface more accurately.

## alpha-v0.1.4 - 2026-03-17

### Highlights

- Rebuilt the Notes inspector around inline sticky-note editing instead of the old split list/editor flow.
- Added note archiving, restore, project-scope grouping under thread headers, and pane-scoped note labels for `THREAD`, `WEB`, `EDIT`, `TERM`, and `DIFF`.
- Flattened the sidebar and pane strip so selection states rely on tinted surfaces and semantic pane accents instead of nested borders.
- Carried pane-type iconography and color across thread rows, pane tabs, strip actions, and terminal shell surfaces for tighter visual consistency.
- Hardened packaging and runtime polish with self-contained RID output fixes, safer window icon loading, and safer project file enumeration in editor panes.

### Included commits

- `Ship WinMux UI, automation, and performance updates`
- `Make RID builds self-contained`
- `Polish notes, pane-strip chrome, and terminal surface accents`

### Detailed changelog

- Notes and inspector
  - Inline note editing now happens directly inside the note card.
  - Project notes are grouped by thread with archived-note disclosure per thread.
  - Notes can move between thread scope and pane scope from the note row itself.
  - Archived note state now persists through session restore and is exposed through native automation.
- Sidebar and pane chrome
  - Thread rows, project rows, and pane tabs now use flatter, lower-chrome selection treatments.
  - Pane badges show consistent browser/editor/diff/terminal iconography across the sidebar and tabs.
  - Pane strip action buttons now use the same semantic accent system as the rest of the shell.
- Terminal and editor surfaces
  - Terminal startup chrome is flatter and less boxed-in.
  - Terminal renderer theme accents now adapt to shell context such as WSL, PowerShell, and Cmd.
  - Editor project file enumeration now skips inaccessible directories and files instead of failing the scan.
- Packaging and runtime
  - Self-contained RID builds now copy the required assets to output more reliably.
  - Build outputs exclude `artifacts/` and generated files that should not compile into the app.
  - Window icon loading now resolves file paths more defensively before calling Win32 icon APIs.

## alpha-v0.1.3

- Fix profile reuse and inspector rebuilds

## alpha-v0.1.2

- Speed up browser pane startup in release builds

## alpha-v0.1.1

- Harden installer UX and prerequisites

## alpha-v0.1.0

- Rename release flow to alpha-v tags

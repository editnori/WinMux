# Changelog

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

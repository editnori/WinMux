# Foundation TODO

This pass is about making the app operable and debuggable before more UI work lands.

## 1. Native automation and screenshots

- [x] Start a native automation endpoint from the running WinUI app.
- [x] Expose shell state for threads, tabs, theme, and active view.
- [x] Expose semantic actions for pane toggle, theme changes, thread switching, tab switching, and screenshot capture.
- [x] Add Bun entrypoints for `health`, `state`, `action`, and `screenshot`.
- [x] Add richer control-level automation with UI-tree snapshots, generic UI actions, terminal inspection, and annotated screenshots.
- [x] Add a few higher-level scripted flows on top of the generic automation layer.
- [x] Expose structured diff-pane inspection and a deterministic patch-review recording flow.
- [x] Fix the fresh WSL terminal startup regression where a second terminal renderer could miss its initial `ready` handshake and never start the ConPTY session.

## 2. Light mode

- [x] Move shell colors into theme dictionaries.
- [x] Propagate theme changes into the embedded terminal renderer.
- [x] Route settings theme changes through the shell instead of only the settings page.
- [ ] Keep tightening contrast and stock control overrides after visual review.

## 3. Projects, threads, and tabs

- [x] Model multiple projects with independent root paths and shell profiles.
- [x] Model threads as nested conversation/session buckets inside each project.
- [x] Model tabs as independent terminal surfaces owned by a single thread.
- [x] Preserve the selected tab per thread when switching projects and threads.
- [x] Add persistence so projects, threads, profiles, and selected tabs survive relaunch.
- [x] Add thread baselines and manual checkpoints for diff review.
- [x] Add a first-pass thread overview surface inside the shell workspace.
- [x] Stop failed restored replay panes from being pruned out of saved sessions.

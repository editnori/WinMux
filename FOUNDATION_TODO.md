# Foundation TODO

This pass is about making the app operable and debuggable before more UI work lands.

## 1. Native automation and screenshots

- [x] Start a native automation endpoint from the running WinUI app.
- [x] Expose shell state for threads, tabs, theme, and active view.
- [x] Expose semantic actions for pane toggle, theme changes, thread switching, tab switching, and screenshot capture.
- [x] Add Bun entrypoints for `health`, `state`, `action`, and `screenshot`.
- [ ] Add richer control-level automation if semantic actions stop being enough.

## 2. Light mode

- [x] Move shell colors into theme dictionaries.
- [x] Propagate theme changes into the embedded terminal renderer.
- [x] Route settings theme changes through the shell instead of only the settings page.
- [ ] Keep tightening contrast and stock control overrides after visual review.

## 3. Projects, threads, and tabs

- [x] Model a project as the root workspace.
- [x] Model threads as independent conversation/session buckets inside the project.
- [x] Model tabs as independent terminal surfaces owned by a single thread.
- [x] Preserve the selected tab per thread when switching threads.
- [ ] Add persistence so projects, threads, and selected tabs survive relaunch.

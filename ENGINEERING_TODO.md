# Engineering TODO

This backlog is the execution plan for the next cleanup phase.

The goal is not abstract "refactor more." The goal is to make WinMux faster to change, easier to measure, and safer to extend without adding more hidden coupling or UI churn.

## Objectives

- [ ] Keep the shell native and dense while continuing to remove stock WinUI friction.
- [ ] Shrink broad monoliths so features land in subsystem files instead of `MainPage.xaml.cs`.
- [ ] Add explicit performance instrumentation so regressions are visible instead of guessed at.
- [ ] Make render invalidation narrower so small state changes stop forcing broad rebuilds.
- [ ] Raise automation and regression coverage around the hot paths we already know are risky.

## Success criteria

- [x] Get [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs) below `8k` LOC.
- [ ] Keep subsystem code in files that roughly map to one surface or workflow.
- [ ] Expose measurable timings for thread switch, pane layout, diff refresh, inspector refresh, and pane materialization.
- [ ] Make it possible to answer "what got slower?" from automation output and logs.
- [ ] Keep feature work landing in components or partials instead of re-growing a single shell file.

## Work order

1. Finish shell decomposition.
2. Introduce performance instrumentation and render invalidation reasons.
3. Split reusable UI surfaces into focused controls.
4. Add perf smoke checks and regression automation.
5. Resume feature work on top of the new boundaries.

## 1. Shell decomposition

### Main shell

- [x] Extract [MainPage.ProjectRail.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.ProjectRail.cs).
- [x] Extract [MainPage.InspectorFiles.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.InspectorFiles.cs).
- [x] Extract [MainPage.GitReview.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.GitReview.cs).
- [x] Extract `MainPage.PaneWorkspace.cs`.
- [x] Extract `MainPage.Notes.cs`.
- [x] Extract `MainPage.SessionRestore.cs`.
- [ ] Extract `MainPage.Automation.cs`.
- [ ] Extract `MainPage.SettingsHost.cs` if settings hosting logic keeps growing.
- [ ] Move subsystem-specific helper types out of `MainPage` when they do not need shell-private state.

### Priorities inside `MainPage`

- [x] Pull pane layout, splitter drag, preview, and zoom logic out of [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs).
- [x] Pull note drafting, note card interactions, and note scope routing out of [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs).
- [x] Pull session restore and pane restoration flow out of [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs).
- [ ] Reduce cross-subsystem field access by grouping shell state more intentionally.

## 2. Component and control extraction

### Candidate controls

- [ ] `Controls/ProjectRailControl`
- [ ] `Controls/PaneWorkspaceControl`
- [ ] `Controls/InspectorFilesControl`
- [ ] `Controls/NotesInspectorControl`
- [ ] `Controls/DiffReviewControl`
- [ ] `Controls/PaneTabStripControl`

### Extraction rules

- [ ] Extract only when the control owns a clear visual surface plus local interaction logic.
- [ ] Keep `MainPage` as orchestration, not as the renderer for every subtree.
- [ ] Preserve existing automation IDs and action hooks during extraction.
- [ ] Avoid "utility dumping-ground" classes with mixed responsibilities.

## 3. Render invalidation cleanup

### Current problem

Small changes still travel too far through the shell and cause avoidable rebuild work.

### Tasks

- [ ] Replace broad string render keys with typed state snapshots where possible.
- [ ] Separate layout invalidation from data invalidation.
- [ ] Separate inspector-review invalidation from inspector-files invalidation.
- [ ] Separate pane-title or note changes from project-rail rebuild conditions.
- [ ] Make pane workspace updates incremental where possible instead of full grid teardown.
- [ ] Track why a render happened, not just that one happened.

## 4. Performance instrumentation

### Core metrics to capture

- [ ] Thread switch duration.
- [ ] Active project rail render duration.
- [ ] Pane workspace render duration.
- [ ] Deferred pane materialization duration.
- [ ] Inspector files refresh duration.
- [ ] Git snapshot refresh duration.
- [ ] Diff pane hydration duration.
- [ ] Session restore duration.

### Plumbing

- [ ] Add a small shell performance collector with a ring buffer of recent events.
- [ ] Stamp each measurement with operation name, thread/project IDs, cache-hit status, and visible pane counts.
- [ ] Record invalidation reason alongside the timing.
- [x] Expose recent perf events through the native automation server.
- [x] Add Bun commands to dump recent perf samples and aggregate counts.

### Output shape

- [x] Support "latest events" output for debugging.
- [x] Support simple aggregate summaries such as avg, p95, and count by operation name.
- [ ] Keep the payload small enough to use frequently during development.

## 5. Hot-path optimization backlog

### Shell workspace

- [ ] Make [MainPage.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/MainPage.xaml.cs) pane-grid updates reuse containers when layout shape is unchanged.
- [ ] Reduce allocation churn in splitter preview building.
- [ ] Revisit deferred pane materialization thresholds once instrumentation exists.
- [ ] Avoid unnecessary focus churn on pane selection and zoom transitions.

### Git and diff

- [ ] Keep tightening snapshot reuse across threads and review modes.
- [ ] Avoid duplicate diff hydration when selection and review source resolve to the same path.
- [ ] Centralize diff-related render state so inspector and visible diff panes do less repeated work.

### Inspector files

- [ ] Continue lazy node materialization.
- [ ] Avoid rebuilding sibling branches when only one expanded path changed.
- [ ] Keep UI-node caches bounded and measurable.

### Browser and editor panes

- [ ] Split [Panes/BrowserPaneControl.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/Panes/BrowserPaneControl.xaml.cs) into theme/chrome/session pieces.
- [ ] Split [Panes/EditorPaneControl.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/Panes/EditorPaneControl.xaml.cs) into editor host, file model, and diff mode pieces if the file keeps growing.
- [ ] Remove duplicated theme and style fallback code outside the shared shell theme path.

## 6. Theme and style cleanup

- [x] Centralize shared shell theme fallbacks in [Shell/ShellTheme.cs](/mnt/c/Users/lqassem/native-terminal-starter/Shell/ShellTheme.cs).
- [ ] Route terminal palette generation through the same theme source instead of hardcoded color tables in [Terminal/TerminalControl.xaml.cs](/mnt/c/Users/lqassem/native-terminal-starter/Terminal/TerminalControl.xaml.cs).
- [ ] Continue removing control-local hardcoded color tables where they duplicate shell resources.
- [ ] Audit small hit targets and narrow chrome once structural refactors settle.

## 7. Automation, testing, and regression safety

### Automation

- [ ] Add automation routes for performance metrics and recent render reasons.
- [ ] Extend native smoke flows to cover:
- [ ] thread switching across large projects
- [ ] diff review source switching
- [ ] checkpoint capture
- [ ] note editing and save
- [ ] pane split and zoom
- [ ] file open from inspector

### Test harness

- [ ] Add a scripted perf-smoke command that performs a known navigation sequence and dumps timings.
- [ ] Add regression assertions for session restore keeping failed panes instead of dropping them.
- [ ] Add regression assertions for active-thread git snapshot reuse.
- [ ] Add regression assertions for inspector lazy tree expansion.

## 8. Observability and tracing

- [ ] Standardize render and perf event names.
- [ ] Define a small event taxonomy for shell, pane, git, inspector, restore, and automation operations.
- [ ] Ensure every high-cost operation logs enough context to diagnose why it ran.
- [ ] Avoid noisy logs by default; keep verbose traces opt-in.

## 9. Repo hygiene

- [ ] Keep stale docs aligned with the actual terminal and WebView2 architecture.
- [ ] Delete dead helper code promptly after replacements land.
- [ ] Prefer one canonical implementation path per subsystem.
- [ ] Keep generated junk and personal artifacts out of tracked files.

## 10. Delivery phases

### Phase A: shrink the shell monolith

- [x] Extract pane workspace.
- [x] Extract notes.
- [x] Extract restore flow.

### Phase B: add performance visibility

- [ ] Add perf collector.
- [x] Add automation exposure.
- [x] Add Bun perf commands.

### Phase C: optimize with evidence

- [ ] Use the new timings to remove the worst remaining UI churn.
- [ ] Set target budgets based on real measurements, not guesses.

### Phase D: feature-ready platform

- [ ] Land new features on the extracted controls and instrumented shell.
- [ ] Reject new feature work that re-expands shell-wide coupling without a strong reason.

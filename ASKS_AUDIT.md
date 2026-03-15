# Ask Audit

This is the current best-effort audit of the historical ask list that led to the present WinMux shell.

It is based on the current repo state, not memory:

- `MainPage.xaml` / `MainPage.xaml.cs`
- `Shell/WorkspaceModels.cs`
- `Panes/BrowserPaneControl.xaml` / `Panes/BrowserPaneControl.xaml.cs`
- `Browser/BrowserCredentialStore.cs`
- `Terminal/TerminalControl.xaml.cs`
- `Git/GitStatusService.cs`
- `Persistence/WorkspaceSessionStore.cs`
- `scripts/run-native-automation-smoke.ps1`
- `scripts/run-terminal-browser-smoke.ps1`
- `AGENTS.md`
- `AUTOMATION_REFERENCE.md`

## Scorecard

These numbers are intentionally approximate because some asks bundled multiple outcomes.

- Verified shipped: `39`
- Partial / landed but not properly finished: `22`
- Still missing: `9`
- One-off operational or research asks not scored as product work: `2`

The main conclusion is:

- the shell is no longer a sample app and a large amount of the ask list really did land
- the biggest missing product work is not basic shell plumbing anymore
- the biggest gap is finishing the second pass on correctness, polish, and depth for browser identity, checkpointed diff history, and large-workspace ergonomics

## Clearly Shipped

### Multiplexor and shell model

- Real thread + pane workspace model exists, not a pile of tiny anonymous splits.
- Visible pane count is explicitly capped at `2-4` per thread through the pane-limit model.
- New non-diff panes overflow into a new thread instead of shrinking forever.
- Threads, panes, projects, and per-thread selected panes are wired together.
- Panes support terminal, browser, editor, and diff surfaces.
- Active pane selection drives the visible active border.
- Pane splitters exist and persist split ratios per thread.
- Inline pane rename exists.
- Thread clear and project clear-all are available through context menus instead of modal-heavy flows.
- Empty-project and empty-thread recovery paths exist.

### Browser and credential baseline

- Browser panes use one shared WinMux WebView2 profile instead of per-project browser profiles.
- Chromium profile seeding and preferred extension import exist.
- Google Passwords CSV import exists.
- Imported credentials are stored in a WinMux-managed encrypted vault using Windows data protection.
- Browser settings disable native autosave/autofill in favor of the WinMux vault.
- New credentials can be captured from browser form submission.
- Preferences exposes import, delete, clear, and manual autofill.
- Browser right-click autofill exists through the WebView2 context menu.
- Multiple matching credentials can be chosen from menus.
- Browser panes expose native automation routes for state, eval, and screenshots.
- WSL terminal/browser bridge exists and has a smoke script.

### Git, diff, and inspector

- Threads are worktree-aware.
- Git state, not terminal text parsing, is the primary diff source of truth.
- The right-side inspector rail exists and is collapsible.
- Changed files show insertion and deletion counts.
- Patch review is moved into a proper diff pane instead of the sidebar.
- Opening a diff file forces a dual-pane review layout.
- Diff coloring exists for additions, removals, and hunk headers.
- Diff state is cleared when the active thread has no valid live selection.
- Light-mode diff rendering exists.

### Session restore, replay, and QA infrastructure

- Workspace session persistence exists across relaunch.
- Restored terminal panes can inject stored replay commands on startup.
- Codex and Claude resume-command tracking exists in terminal state.
- Native automation coverage is substantial: shell state, UI tree, UI actions, browser state, terminal state, screenshots, recordings, render traces, event logs, desktop window control, and desktop UIA fallback.
- There is a serious smoke baseline already, including thread creation, pane creation, diff opening, settings, theme switching, thread duplication, empty-state recovery, recordings, and event logging.
- Light-mode selected-thread regression coverage exists in smoke.

### Small asks that are visibly done

- `t3.jpeg` is not present anymore.
- Sample patch artifacts exist under `tmp/`, including `tmp/diff-example.patch`.

## Partial Or Brittle

These asks landed in spirit, but not to the level the original request implied.

### Multiplexor / navigation

- The overflow model is present, but the stronger `niri` ask is only partly landed.
  What exists: pane caps, overflow-thread creation, denser workspace behavior.
  What is still missing from the original feel: thread overview / true vertical workspace navigation.
- Multi-surface panes landed for browser/editor/diff, but the future captured app/window pane idea did not.
- The shell clearly stopped depending on WSL shutdown as a reset habit, but that behavior is more an absence of a bad pattern than an explicit product feature.

### Browser usability and identity

- Browser startup/profile behavior is much better than before, but still not at live-Chrome parity.
- The repo explicitly documents that shared WinMux profile seeding is not the same as real Chrome-profile reuse or Google Sync.
- The browser is usable for agent workflows through native routes and the terminal bridge, but it is still a single-live-page pane model.
- The ask to avoid two confusing password managers is only partly solved.
  WinMux disables WebView2 native password features, which helps.
  But the user still ends up with Chrome's world and WinMux's world as separate systems.
- Browser credential controls exist, but they are functional more than polished.
- The original nav-bar focus/usability complaint is hard to score as fully closed from code alone.
  The address bar and controls are now straightforward and automation-visible.
  There is no strong regression coverage specifically for the original bug.

### Resize, theme, replay, and continuity

- Pane resize works and split ratios persist, but "more natural" resize/scale consistency still needs a polish pass.
- Browser half-cutoff risk is mitigated by layout invalidation and resize notifications, but there is no tight regression proof that the old split-view clipping issue is fully dead.
- Light mode is materially better and smoke-covered for the selected-thread row, but color consistency still is not fully audited across the whole shell.
- Grey flashes on thread switching are not obviously modeled as a closed issue; there are render coalescing optimizations, not a clear targeted fix.
- Session replay exists, but it is replay-command injection, not true process hibernation.
- Resume-command persistence for `codex` / `claude` is good enough for the common path, but it is still regex-driven and should be treated as "helpful replay recovery", not a perfect session model.

### Git / diff / inspector quality

- The inspector is much cleaner than a debug panel, but it is still mostly a repo-state rail, not the fuller merged directory/diff/project inspector concept.
- Patch preview fallback behavior is improved, but still text-and-error driven.
- Thread-start baseline and manual checkpoint history now exist, but the review UX is still snapshot-oriented rather than a richer timeline browser.
- The ask about Codex and Claude workflow modeling is partly reflected in replay metadata and worktree-aware threads, but there is no richer assistant-aware thread model beyond pane metadata.

### Thread rail, context actions, and performance

- Context actions are better, but still narrow.
  Project menu: new thread, clear all threads.
  Thread menu: rename, duplicate, clear thread.
  Pane menu: rename.
- Clear-thread and clear-all-threads stability looks materially improved and is smoke-covered, but "try to break everything" review is still only partly systematized.
- Performance work happened.
  Project tree rendering is keyed and coalesced.
  Git refresh moved off the UI thread.
  Render keys reduce redundant workspace work.
  But there is no evidence of a deeper large-thread-count audit or measurement pass.
- Top controls are reasonably aligned, but the shell still carries the stock `TabView` weight the docs already call out.

## Still Missing

These are the clearest asks that are not really done yet.

- A more polished `niri`-style thread overview / vertical workspace navigation pass.
- External captured app/window preview panes as first-class workspace panes.
- A fuller assistant/thread model that treats Codex and Claude as first-class thread participants instead of just pane metadata and replay commands.
- A cleaner merged inspector that can absorb directory/project context instead of only repo-state + changed files.
- A real large-workspace performance pass with evidence, not just tactical render coalescing.
- A full visual/polish pass on top chrome and pane strip, including reducing the remaining stock `TabView` feel.
- A stronger, explicit closure on browser resize/focus edge cases instead of relying on indirect fixes.

## Not Scored

- "Research Codex and Claude workflows in depth" is partly reflected in implementation choices, but it is not a product surface that can be cleanly scored as shipped or not shipped.
- "Launch WinMux in a real light-mode patch review state so you could inspect it live" was an operational request, not durable product work.

## Review Findings

These are the main prescriptive findings from the audit.

### 1. The product moved from foundation work to second-pass quality work

The repo already contains the hard foundation pieces:

- workspace model
- multi-pane thread model
- browser pane
- credential vault
- inspector rail
- diff pane
- session persistence
- replay capture
- automation and smoke coverage

The next pass should stop acting like the shell is still missing its base architecture. It is not.

### 2. The browser story is useful, but still strategically unresolved

The current browser implementation is good enough for:

- previewing pages
- inspecting browser state
- agent/browser bridge flows
- imported credentials and manual/context autofill

It is still not good enough for:

- true Chrome parity
- a clean single-password-manager story
- multi-page workflows inside one browser surface
- confidence that the original focus/resize complaints are fully closed

### 3. Diff and inspector work is solid, but history is still absent

The current git/diff system is live-state oriented:

- current working tree
- changed file list
- selected diff
- thread worktree path

What is still missing is the more durable model the asks were pushing toward:

- thread start baseline
- checkpoint snapshots
- reviewable history over time

Without that, the inspector is useful, but not yet the full thread-review system that was being requested.

### 4. QA is real now, but the review standard should rise again

Smoke coverage is not the problem anymore. The problem is where smoke ends:

- browser identity edge cases
- session restore fidelity
- replay correctness
- focus bugs
- resize bugs
- large-thread performance
- visual polish regressions

That is where the next review effort should go.

### 5. Docs are inconsistent

- `AGENTS.md` reflects the current app shape well.
- `README.md` and `FOUNDATION_TODO.md` are now aligned with the current worktree state.
- `AUTOMATION_REFERENCE.md` still has some stale sections around missing persisted restore and higher-level scripted flows.

## Concrete Fixups Found During Review

- Fixed in this pass: pane splitter drags now queue a session save, so updated split ratios persist without waiting for some unrelated later mutation.
- Fixed in this pass: semantic `refreshDiff` / `selectDiffFile` actions no longer depend on the old synchronous git capture path.
- Fixed in this pass: selected diff state is now persisted per thread and restored with the workspace snapshot.
- Fixed in this pass: restore/load failures are no longer silent; they are now logged as restore failures.
- Fixed in this pass: browser CSV import now merges into the existing WinMux vault instead of replacing it wholesale.
- Fixed in this pass: the terminal browser bridge now exposes screenshot support alongside `state` and `eval`.
- Fixed in this pass: git-selected patch capture no longer loses the target path when WinMux shells out through `wsl.exe`, so real per-file diffs render again instead of falling back to "Patch unavailable for this file."
- Fixed in this pass: diff panes now expose structured patch state and resolved line colors through native automation, plus a dedicated patch-review recording flow.
- Fixed in this pass: diff-pane light-mode brushes now resolve through the active shell theme instead of sticking to dark-mode colors.
- Already true in the current worktree: `README.md` and `FOUNDATION_TODO.md` now reflect the real product state.
- Newly visible during this pass: fresh WSL terminal panes can still trip a `WebView2 failed: Object reference not set to an instance of an object.` startup regression, and that is currently the main blocker on a fully green end-to-end smoke.

## Recommended Next Pass

### P0: correctness and product depth

- Close the browser identity story.
  Decide whether WinMux is only a practical imported-vault browser, or whether it is trying to approximate a signed-in browser surface more aggressively.
  Then shape the UI and docs around that decision.
- Add targeted regression coverage for the original browser paper cuts.
  Focus on nav focus, split-view resize, half-cutoff behavior, and credential autofill flows.
- Tighten session replay fidelity.
  Treat current replay support as best-effort recovery and harden it with explicit tests for Codex and Claude resume commands plus preserved launch flags.

### P1: shell and inspector quality

- Deepen the new thread/workspace overview into a more polished vertical workspace navigator.
- Replace more of the stock `TabView` feel with a thinner custom pane strip.
- Expand context actions for threads, projects, and panes.
- Merge more project context into the inspector so it feels like a real companion rail, not only a git rail.

### P1: performance and QA

- Measure thread-switch and rail-refresh latency with large project/thread counts.
- Add a repeatable "abuse" test pass on top of `native:smoke`.
- Add automated coverage for session restore, browser credentials, and replay restoration.

### P2: optional but aligned with the original direction

- External captured app/window panes.
- Richer diff presentation than raw text coloring once the history model exists.

## Recommended Order

If this backlog is going to be reviewed and executed in order, the clean sequence is:

1. browser identity and browser regression closure
2. session replay hardening
3. vertical workspace/thread overview polish
5. large-workspace performance pass
6. top-chrome and pane-strip visual cleanup

That sequence matches where the real product risk is now.

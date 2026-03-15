# Automation Reference

This file is the source-of-truth reference for what the native `WinMux` app can currently expose and what is still missing.

## Entry points

Native automation server:

- `GET /health`
- `GET /state`
- `GET /ui-tree`
- `GET /desktop-windows`
- `GET /recording-status`
- `GET /events`
- `POST /action`
- `POST /ui-action`
- `POST /desktop-action`
- `POST /terminal-state`
- `POST /browser-state`
- `POST /diff-state`
- `POST /browser-eval`
- `POST /browser-screenshot`
- `POST /recording/start`
- `POST /recording/stop`
- `POST /render-trace`
- `POST /screenshot`
- `POST /events/clear`

External semantic desktop automation helper:

- `scripts/run-desktop-uia.ps1 tree <json>`
- `scripts/run-desktop-uia.ps1 action <json>`

Bun wrappers:

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
bun run native:desktop-windows
bun run native:desktop-uia-tree
bun run native:events
bun run native:events:clear
bun run native:recording-status
bun run native:action -- '{"action":"newThread"}'
bun run native:action -- '{"action":"newBrowserPane"}'
bun run native:action -- '{"action":"newEditorPane"}'
bun run native:action -- '{"action":"setLayout","value":"3"}'
bun run native:ui-action -- '{"action":"click","automationId":"shell-nav-settings"}'
bun run native:desktop-action -- '{"action":"focusWindow","titleContains":"WinMux"}'
bun run native:desktop-uia-action -- '{"action":"invoke","titleContains":"Browse for Folder","name":"OK"}'
bun run native:terminal-state
bun run native:browser-state
bun run native:diff-state
bun run native:browser-eval -- "<pane-id>" "document.title"
bun run native:browser-screenshot -- "<pane-id>"
bun run native:agent-browser-smoke
bun run native:recording-start -- '{"fps":24,"maxDurationMs":5000,"keepFrames":false}'
bun run native:recording-stop
bun run native:demo-recording
bun run native:demo-recording:cinematic
bun run native:patch-review-recording
bun run native:new-project-recording
bun run native:render-trace
bun run native:screenshot
bun run native:screenshot:annotated
bun run native:smoke
```

## What I Can Control

### Semantic shell actions

These go through `POST /action`.

Supported `action` values:

- `togglePane`
- `toggleInspector`
- `showTerminal`
- `showSettings`
- `newProject`
- `newThread`
- `newTab`
- `newBrowserPane`
- `newEditorPane`
- `importBrowserPasswordsCsv`
- `deleteBrowserCredential`
- `clearBrowserCredentials`
- `autofillBrowser`
- `selectProject`
- `selectThread`
- `selectTab`
- `moveTabAfter`
- `closeTab`
- `setLayout`
- `setThreadWorktree`
- `refreshDiff`
- `selectDiffFile`
- `navigateBrowser`
- `setTheme`
- `setProfile`
- `renameThread`
- `duplicateThread`
- `deleteThread`
- `clearProjectThreads`
- `deleteProject`
- `input`

Relevant payload fields:

- `projectId`
- `threadId`
- `tabId`
- `targetTabId`
- `value`

Notes:

- `newProject` on the semantic route creates a project directly from `value` without opening the dialog.
- `toggleInspector` collapses or reopens the right-side inspector rail without changing the active thread.
- `newBrowserPane` adds a preview pane to the active thread backed by the shared WinMux browser profile.
- `newEditorPane` adds a terminal-backed editor pane that launches `nvim .`.
- `importBrowserPasswordsCsv` imports a Google Passwords CSV into the WinMux-encrypted credential store.
- `deleteBrowserCredential` removes one imported credential from the WinMux vault by credential id.
- `clearBrowserCredentials` clears the imported WinMux credential vault.
- `autofillBrowser` triggers a manual autofill attempt on the selected browser pane.
- `setLayout` accepts `1|2|3|4` or `solo|dual|triple|quad`.
- `setThreadWorktree` binds a thread to a different repo/worktree path for new panes, diff state, header text, and session replay.
- `refreshDiff` refreshes the active thread's git snapshot.
- `selectDiffFile` refreshes the active thread's git snapshot, selects one changed file by relative path, and opens or updates the thread's diff pane in a dual-pane view.
- `clearProjectThreads` removes all threads from the target project while keeping the project shell open.
- `navigateBrowser` sends a URL to the selected browser pane; an empty `value` returns it to the built-in start page.
- `deleteProject` removes a project and its threads from the live workspace; if it was the active project, WinMux activates the next surviving project.
- `input` sends text to the selected terminal/editor pane, not to arbitrary native controls.
- `moveTabAfter` provides a semantic pane reorder path without coordinate dragging.

### Git-aware thread state

`GET /state` now includes git metadata for the active thread:

- `gitBranch`
- `worktreePath`
- `changedFileCount`
- `selectedDiffPath`
- `inspectorOpen`

Each thread entry also includes:

- `worktreePath`
- `branchName`
- `selectedDiffPath`
- `changedFileCount`

Notes:

- git snapshots refresh on thread activation and can be forced with `refreshDiff`
- git snapshots now refresh off the UI thread and use direct `wsl.exe` git probes instead of repeated PowerShell launches
- non-git threads show a friendly "No git repository detected for this thread." message instead of raw git stderr

### Browser inspection

These routes expose browser-pane state directly from the native app, which is now the preferred browser debug path.

`POST /browser-state` returns:

- selected browser pane id
- title
- URI
- address-box text
- initialization state
- profile seed status
- extension import status
- credential autofill status
- imported preferred extension names

Notes:

- imported credentials are stored in a WinMux-managed encrypted vault, not in WebView2's native password database
- imported credentials can autofill matching pages such as Google sign-in username fields

`POST /browser-eval` executes JavaScript inside a browser pane and returns the raw `ExecuteScriptAsync` result.

Request fields:

- `paneId`
- `script`

`POST /browser-screenshot` captures the current browser-pane preview to a PNG file.

Request fields:

- `paneId`
- `path`

Notes:

- browser panes share one WinMux WebView2 profile even in debug mode
- this means browser inspection no longer depends on the shared CDP target list
- the preferred extension set is currently Claude plus uBlock Origin when found in the local Chromium profile
- the profile can be seeded or repaired from local Chromium data, but it still does not imply live Chrome-profile reuse or Google Sync parity

### Diff-pane inspection

`POST /diff-state` returns structured patch-review state directly from the native diff pane.

Returned fields per pane:

- `paneId`
- `threadId`
- `projectId`
- `title`
- `path`
- `summary`
- `rawText`
- `hasDiff`
- `lineCount`
- `lines`

Each diff line includes:

- `index`
- `kind`
- `text`
- `foreground`

Request fields:

- `paneId`
- `maxLines`

Notes:

- this is the native source of truth for patch-review validation now
- line `kind` is normalized to `metadata`, `hunk`, `addition`, `deletion`, `context`, or `empty`
- `foreground` is the resolved shell color, so light-mode and dark-mode diff audits can assert actual rendered brush choices
- diff panes now expose inner semantic ids such as `shell-diff-pane-header-<pane-id>`, `shell-diff-pane-path-<pane-id>`, `shell-diff-pane-summary-<pane-id>`, and `shell-diff-pane-content-<pane-id>`

### Terminal-side browser bridge

WinMux also exposes browser automation into terminal panes and WSL shells through environment variables and a small helper script.

Current pieces:

- `WINMUX_AUTOMATION_URL`
- `WINMUX_BROWSER_STATE_URL`
- `WINMUX_BROWSER_EVAL_URL`
- `WINMUX_BROWSER_SCREENSHOT_URL`
- `WINMUX_BROWSER_PROFILE_MODE`
- `WINMUX_REPO_ROOT`
- `tools/winmux_browser_bridge.py`

The bridge is currently verified by:

- `bun run native:agent-browser-smoke`

Notes:

- WSL terminal agents should use the helper bridge instead of assuming direct CDP access to browser panes
- the helper currently supports browser-state, browser-eval, and browser-screenshot style queries against the live WinMux browser pane

### Generic UI actions

These go through `POST /ui-action`.

Supported `action` values:

- `focus`
- `click`
- `invoke`
- `doubleClick`
- `rightClick`
- `setText`
- `select`
- `toggle`
- `hover`
- `press`
- `normalState`
- `invokeMenuItem`

Supported targeting fields:

- `automationId`
- `refLabel`
- `elementId`
- `name`
- `text`

Extra payload fields:

- `value`
- `menuItemText`

Notes:

- `refLabel` maps to the `e1`, `e2`, `e3` labels from annotated screenshots and `ui-refs`.
- `setText` works on `TextBox` and `ComboBox`.
- `invokeMenuItem` works on controls that expose a `ContextFlyout`.
- `hover`, `press`, and `normalState` drive WinUI visual states for inspection. They are useful for checking hover and pressed styling even though they are not full pointer simulation.

Examples of WinMux semantic automation ids now exposed on dynamic shell content:

- `shell-project-title-<project-id>`
- `shell-project-meta-<project-id>`
- `shell-thread-title-<thread-id>`
- `shell-thread-meta-<thread-id>`
- `shell-tab-header-<pane-id>`
- `shell-tab-kind-<pane-id>`
- `shell-tab-title-<pane-id>`
- `shell-diff-file-<path-key>`
- `shell-diff-file-status-<path-key>`
- `shell-diff-file-name-<path-key>`
- `shell-diff-file-meta-<path-key>`
- `shell-diff-file-metrics-<path-key>`
- `doubleClick` is supported for app-owned cases such as thread rename.

### Desktop window automation

These routes expose top-level and child windows outside the WinUI visual tree.

`GET /desktop-windows` returns:

- top-level windows
- child window handles
- window titles
- class names
- bounds
- visibility
- focus state

`POST /desktop-action` supports:

- `focusWindow`
- `clickPoint`
- `doubleClickPoint`
- `rightClickPoint`
- `hoverPoint`
- `dragPoint`
- `moveWindow`
- `centerWindow`
- `resizeWindow`
- `maximizeWindow`
- `setTopmost`
- `sendKeys`
- `typeText`

Relevant targeting fields:

- `handle`
- `titleContains`
- `className`
- `width`
- `height`

Notes:

- if `handle` is provided and no coordinates are given, click actions target the window center
- if coordinates are provided alongside a `handle`, they are treated as offsets from that window’s top-left corner
- `resizeWindow` uses `width` and `height` to resize the outer native window, which is useful for higher-resolution captures
- `moveWindow`, `centerWindow`, `maximizeWindow`, and `setTopmost` are the main window-state controls for reliable native recordings
- `sendKeys` supports common chords such as `Ctrl+A`, `Shift+Tab`, `F2`, `Delete`, and `Esc`
- `typeText` types through Win32 keyboard injection, so it works against external picker/edit controls if they have focus

This is the path for OS-owned UI such as the external Windows folder picker.

### External desktop UIA

This is a separate PowerShell-backed semantic helper, not an app-hosted HTTP route.

`bun run native:desktop-uia-tree` returns:

- a semantic UI Automation tree for a matching external window
- interactive descendants with stable `elementId` paths
- automation ids, names, class names, control types, bounds, selection, expansion, and checked states

Request fields:

- `handle`
- `titleContains`
- `className`
- `maxDepth`

`bun run native:desktop-uia-action` supports:

- `focus`
- `invoke`
- `click`
- `setValue`
- `setText`
- `select`
- `toggle`
- `expand`
- `collapse`

Targeting fields:

- `handle`
- `titleContains`
- `className`
- `elementId`
- `automationId`
- `name`
- `text`
- `value`

This is the semantic path for external windows such as the Windows folder picker after it opens.

### Native UI discovery

`GET /ui-tree` returns:

- `windowTitle`
- `activeView`
- `root`
- `interactiveNodes`

Each `interactiveNodes` item includes:

- `elementId`
- `refLabel`
- `automationId`
- `name`
- `controlType`
- `text`
- `visible`
- `enabled`
- `focused`
- `selected`
- `checked`
- `x`
- `y`
- `width`
- `height`
- `margin`
- `padding`
- `borderThickness`
- `cornerRadius`
- `background`
- `borderBrush`
- `foreground`
- `opacity`
- `fontSize`
- `fontWeight`

Important behavior:

- open dialogs and popup content are included in the tree
- collapsed or hidden controls are filtered out
- the overlay canvas used for annotated screenshots is excluded

### Terminal inspection

`POST /terminal-state` returns:

- `selectedTabId`
- `tabs[]`

Each tab snapshot includes:

- `tabId`
- `threadId`
- `projectId`
- `title`
- `displayWorkingDirectory`
- `shellCommand`
- `rendererReady`
- `started`
- `cols`
- `rows`
- `cursorX`
- `cursorY`
- `selection`
- `visibleText`
- `bufferTail`

This is enough for:

- checking which tab is active
- confirming terminal startup completed
- reading the current visible rows
- reading tail scrollback
- checking cursor movement
- confirming shell profile and working directory

### Event log

`GET /events` returns a bounded in-memory event stream with:

- `sequence`
- `timestamp`
- `category`
- `name`
- `message`
- `data`

`POST /events/clear` clears the current buffer.

This is the primary way to inspect transient shell behavior such as:

- thread creation and selection
- tab creation and selection
- tab view refreshes
- terminal fit requests
- terminal renderer ready / resize
- terminal title changes
- screenshot captures
- automation action execution

### Render trace

`POST /render-trace` captures a bounded sequence of native render frames.

Request fields:

- `frames`
- `captureScreenshots`
- `annotated`
- optional nested `action`
- optional nested `uiAction`

Each returned frame can include:

- timestamp
- shell state
- interactive nodes
- optional screenshot path

This is the main tool for transient native behaviors like tab insertion, rerender churn, and quick shell-state transitions.

### Native recording

`GET /recording-status` returns the current recorder state.

`POST /recording/start` supports:

- `fps`
- `maxDurationMs`
- `jpegQuality`
- `outputDirectory`
- `keepFrames`

`POST /recording/stop` finalizes the frame capture and returns:

- `recordingId`
- `outputDirectory`
- `capturedFrames`
- `manifestPath`
- optional `videoPath`

Important behavior:

- the recorder captures native window frames directly from the running app
- it writes a frame manifest even if `ffmpeg` is not installed
- if `ffmpeg` is available, it also encodes an `.mp4`
- frame JPEGs are deleted after a successful encode unless `keepFrames` is `true`
- this is the highest-fidelity native visual capture path for animation review right now

### Demo recording

`bun run native:demo-recording` runs a paced walkthrough of the major shell behaviors and saves:

- `recording.mp4`
- `manifest.json`

`bun run native:demo-recording:cinematic` runs a slower and higher-resolution variant.

`bun run native:new-project-recording` isolates the new-project dialog flow, keeps frames by default, and validates the missing-directory WSL startup path.

`bun run native:patch-review-recording` creates a temporary git project, opens a real diff pane for `notes.txt`, waits for structured diff-state to report rendered hunk/add/remove lines, then saves a screenshot plus native recording artifacts.

The demo currently covers:

- pane collapse and expand
- terminal input
- thread creation and rename
- tab creation, switching, close, and semantic reorder
- settings view
- theme switching
- shell-profile switching
- thread duplication, rename, and delete
- new-project dialog
- empty-project recovery
- project switching

Useful knobs on `scripts/run-native-demo-recording.ps1`:

- `-Mode standard|cinematic`
- `-Fps`
- `-WindowWidth`
- `-WindowHeight`
- `-KeepFrames`

### Screenshots

`POST /screenshot` supports:

- `path`
- `annotated`

If `annotated` is `true`, the app overlays ref labels on visible interactive controls before capture.

## Behaviors Covered By `native:smoke`

The current smoke run validates:

- automation health
- initial shell state
- visible interactive node discovery
- add thread
- add tab
- semantic tab reorder
- terminal readiness and metrics
- desktop UIA tree discovery
- desktop UIA action
- settings discovery
- theme switching
- duplicate thread through context menu
- rename thread through dialog automation
- delete thread while the project remains open
- create project through dialog automation
- delete the only thread in a project and confirm the empty state
- recover from the empty project state by creating a new thread
- annotated screenshot capture
- desktop window enumeration
- native recording start/stop
- render trace capture
- event logging

This is the current regression baseline for native automation coverage.

## What I Still Cannot Fully Control

### OS-owned surfaces

These are not app-owned UI, but they are now partly controllable:

- the Windows folder picker launched by `Browse...`
- system dialogs and external shell windows that expose Win32 handles or UIA trees

Current coverage:

- discoverable through `desktop-windows`
- semantically inspectable through `desktop-uia-tree`
- actionable through `desktop-action` and `desktop-uia-action`

Remaining gap:

- there is still no app-owned wrapper around every possible third-party or system-specific UIA pattern

### Render-time and animation introspection

I now have:

- event sequencing
- multi-frame render trace capture
- optional per-frame screenshots

That is enough to inspect many transient behaviors around tab creation, layout refreshes, and quick native rerenders.

I still do not have:

- automatic diff summaries between successive native frames
- guaranteed smooth capture on every machine at the highest requested FPS, because capture is screen-copy based

### Style and spacing introspection

I now have per-element metadata for:

- resolved solid-color foreground/background brushes
- border brush
- margin and padding
- border thickness
- corner radius
- opacity
- font size and font weight

I still do not have:

- theme resource key provenance
- detailed template part ownership
- non-solid brush breakdown beyond brush type

So if the question is:

- “which container has the wrong padding?”
- “which tab is resolving the wrong selected background?”
- “which element is using the wrong brush in light mode?”

I still have to infer that from XAML/resources and screenshots rather than ask the app directly.

### Pointer nuance

I now have:

- app-owned hover/pressed visual-state control
- app-owned double-click rename
- real coordinate pointer injection
- drag support through desktop actions
- semantic tab reorder through `moveTabAfter`

I still do not have:

- element-aware semantic drag for arbitrary shell objects beyond the tab reorder path

### Keyboard nuance outside the terminal

I now have generic Win32 key injection through desktop actions, which covers:

- `Tab`
- `Shift+Tab`
- `F2`
- `Delete`
- `Esc`
- common modifier chords

The remaining limitation is that this is focus-driven keyboard injection, not semantic intent. I still have to make sure the right native or desktop control has focus first.

### Some product behaviors do not exist yet

These are not automation gaps, they are app-feature gaps:

- true ConPTY process hibernation beyond workspace replay
- thread-start / checkpoint diff history beyond the current live git snapshot

## What I Ideally Want Next

If the goal is “full freedom and control,” these are the highest-leverage additions.

### 1. Render/layout snapshots with style data

Add an endpoint that returns per-element:

- margin
- padding
- actual size
- desired size
- background brush color
- foreground brush color
- border brush color
- corner radius
- font size
- font weight

This would solve color consistency, spacing, and container-tagging problems directly.

### 2. Frame diffing and richer recording analysis

Add a layer on top of the existing recorder and render trace that captures:

- per-frame tree diffs
- layout deltas by element id
- event log correlation per frame
- a summary of what changed between successive frames

This is the missing piece for “I clicked new tab and something flashed weird for 150ms.”

### 3. Explicit style/debug tagging

Add optional automation IDs or debug names for:

- top-level containers
- tab strip subparts
- header regions
- sidebar sections
- settings sections

Right now many controls are discoverable, but some layout containers still have generic type-only identities.

## Current Bottom Line

For app-owned shell behavior, I now have strong control over:

- project/thread/tab state
- settings
- dialogs
- context menus
- delete-thread and threadless project recovery
- terminal inspection
- event logging
- hover/pressed visual-state inspection
- desktop window enumeration and external window actions
- semantic external UIA inspection and actions
- native frame recording with manifest output and optional video encoding
- semantic tab reorder without coordinate dragging
- render trace capture
- annotated screenshots

The biggest remaining observability gap is richer frame-to-frame diffing and more semantic drag/reorder support outside the current tab path, not basic access.

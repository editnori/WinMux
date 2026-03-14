# Automation Reference

This file is the source-of-truth reference for what the native `WinMux` app can currently expose and what is still missing.

## Entry points

Native automation server:

- `GET /health`
- `GET /state`
- `GET /ui-tree`
- `GET /events`
- `POST /action`
- `POST /ui-action`
- `POST /terminal-state`
- `POST /screenshot`
- `POST /events/clear`

Bun wrappers:

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
bun run native:events
bun run native:events:clear
bun run native:action -- '{"action":"newThread"}'
bun run native:ui-action -- '{"action":"click","automationId":"shell-nav-settings"}'
bun run native:terminal-state
bun run native:screenshot
bun run native:screenshot:annotated
bun run native:smoke
```

## What I Can Control

### Semantic shell actions

These go through `POST /action`.

Supported `action` values:

- `togglePane`
- `showTerminal`
- `showSettings`
- `newProject`
- `newThread`
- `newTab`
- `selectProject`
- `selectThread`
- `selectTab`
- `closeTab`
- `setTheme`
- `setProfile`
- `renameThread`
- `duplicateThread`
- `deleteThread`
- `input`

Relevant payload fields:

- `projectId`
- `threadId`
- `tabId`
- `value`

Notes:

- `newProject` on the semantic route creates a project directly from `value` without opening the dialog.
- `input` sends text to the selected terminal tab, not to arbitrary native controls.

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
- `doubleClick` is supported for app-owned cases such as thread rename.

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
- terminal readiness and metrics
- settings discovery
- theme switching
- duplicate thread through context menu
- rename thread through dialog automation
- delete thread while the project remains open
- create project through dialog automation
- delete the only thread in a project and confirm the empty state
- recover from the empty project state by creating a new thread
- annotated screenshot capture
- event logging

This is the current regression baseline for native automation coverage.

## What I Still Cannot Fully Control

### OS-owned surfaces

These are not app-owned UI:

- the Windows folder picker launched by `Browse...`
- any system modal or external shell window

The app-owned new-project dialog is automatable. The external picker is not.

### Render-time and animation introspection

I now have a native event stream and can inspect sequencing around render-time behavior, but I still do not have true frame-by-frame tracing for:

- tab insert/remove animation phases
- subtle layout shifts during tab creation
- transient loading badges or flashes that appear between frames
- frame-by-frame sidebar or header transitions

Today I can catch those by combining:

- the event log
- repeated screenshot capture
- inspecting the final UI tree
- comparing terminal-state or shell-state before and after

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

I now have app-owned visual-state controls for hover/pressed inspection and an app-owned double-click path for thread rename.

I still do not have:

- drag
- click at arbitrary coordinates
- resize drag handles
- true physical pointer move/click paths

### Keyboard nuance outside the terminal

I can send terminal text via the semantic `input` action, but I do not yet have generic native shell key simulation for:

- `Tab`
- `Shift+Tab`
- `F2`
- `Delete`
- accelerators against arbitrary focused WinUI controls

### Some product behaviors do not exist yet

These are not automation gaps, they are app-feature gaps:

- split panes inside a tab
- persisted workspace/session restore

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

### 2. Animation/frame tracing

Add an opt-in trace around UI actions that captures:

- pre-action UI tree
- post-layout UI tree
- post-animation UI tree
- screenshots at each phase

This is the missing piece for “I clicked new tab and something flashed weird for 150ms.”

### 3. Native pointer and keyboard primitives

Add UI actions for:

- `doubleClick`
- `hover`
- `pressKey`
- `pressChord`
- `clickPoint`
- `drag`

That would cover rename-on-double-click, hover states, drag reorder, and coordinate-sensitive polish work.

### 4. Explicit style/debug tagging

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
- annotated screenshots

The biggest remaining observability gap is frame-by-frame render tracing and true native pointer/keyboard simulation.

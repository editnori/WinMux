# Automation Reference

This file is the source-of-truth reference for what the native `WinMux` app can currently expose and what is still missing.

## Entry points

Native automation server:

- `GET /health`
- `GET /state`
- `GET /ui-tree`
- `POST /action`
- `POST /ui-action`
- `POST /terminal-state`
- `POST /screenshot`

Bun wrappers:

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
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
- `rightClick`
- `setText`
- `select`
- `toggle`
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
- create project through dialog automation
- annotated screenshot capture

This is the current regression baseline for native automation coverage.

## What I Still Cannot Fully Control

### OS-owned surfaces

These are not app-owned UI:

- the Windows folder picker launched by `Browse...`
- any system modal or external shell window

The app-owned new-project dialog is automatable. The external picker is not.

### Render-time and animation introspection

I can inspect before/after state and capture screenshots, but I do not yet have a native timeline for:

- tab insert/remove animation phases
- subtle layout shifts during tab creation
- transient loading badges or flashes that appear between frames
- frame-by-frame sidebar or header transitions

Today I can catch those only by:

- repeated screenshot capture
- inspecting the final UI tree
- comparing terminal-state or shell-state before and after

### Style and spacing introspection

I do not yet have a structured endpoint for:

- resolved foreground/background brushes per element
- margin and padding per element
- corner radius per element
- effective font size and weight per element
- theme resource key provenance

So if the question is:

- “which container has the wrong padding?”
- “which tab is resolving the wrong selected background?”
- “which element is using the wrong brush in light mode?”

I still have to infer that from XAML/resources and screenshots rather than ask the app directly.

### Pointer nuance

I do not yet have native automation primitives for:

- double click
- hover
- drag
- click at arbitrary coordinates
- resize drag handles

Current native automation is element-driven, not pointer-path-driven.

### Keyboard nuance outside the terminal

I can send terminal text via the semantic `input` action, but I do not yet have generic native shell key simulation for:

- `Tab`
- `Shift+Tab`
- `F2`
- `Delete`
- accelerators against arbitrary focused WinUI controls

### Some product behaviors do not exist yet

These are not automation gaps, they are app-feature gaps:

- delete thread while keeping the project
- double-click to rename a thread
- split panes inside a tab
- event stream for tab/thread lifecycle
- persisted workspace/session restore

## What I Ideally Want Next

If the goal is “full freedom and control,” these are the highest-leverage additions.

### 1. Native event log

Add a structured event stream or ring buffer for:

- tab added
- tab removed
- tab selected
- thread selected
- thread renamed
- project added
- settings opened
- theme changed
- terminal ready
- terminal exited

With timestamps, this would make transient behavior debuggable without guessing.

### 2. Render/layout snapshots with style data

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

### 3. Animation/frame tracing

Add an opt-in trace around UI actions that captures:

- pre-action UI tree
- post-layout UI tree
- post-animation UI tree
- screenshots at each phase

This is the missing piece for “I clicked new tab and something flashed weird for 150ms.”

### 4. Native pointer and keyboard primitives

Add UI actions for:

- `doubleClick`
- `hover`
- `pressKey`
- `pressChord`
- `clickPoint`
- `drag`

That would cover rename-on-double-click, hover states, drag reorder, and coordinate-sensitive polish work.

### 5. Explicit style/debug tagging

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
- terminal inspection
- annotated screenshots

The biggest remaining observability gap is not clicking controls. It is seeing transient render-time behavior and resolved layout/style data.

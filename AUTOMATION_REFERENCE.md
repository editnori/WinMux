# Automation Reference

This file is the source-of-truth reference for what the native `WinMux` app can currently expose and what is still missing.

## Entry points

Native automation server:

- `GET /health`
- `GET /state`
- `GET /ui-tree`
- `GET /desktop-windows`
- `GET /events`
- `POST /action`
- `POST /ui-action`
- `POST /desktop-action`
- `POST /terminal-state`
- `POST /render-trace`
- `POST /screenshot`
- `POST /events/clear`

Bun wrappers:

```bash
bun run native:health
bun run native:state
bun run native:ui-tree
bun run native:ui-refs
bun run native:desktop-windows
bun run native:events
bun run native:events:clear
bun run native:action -- '{"action":"newThread"}'
bun run native:ui-action -- '{"action":"click","automationId":"shell-nav-settings"}'
bun run native:desktop-action -- '{"action":"focusWindow","titleContains":"WinMux"}'
bun run native:terminal-state
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
- `sendKeys`
- `typeText`

Relevant targeting fields:

- `handle`
- `titleContains`
- `className`

Notes:

- if `handle` is provided and no coordinates are given, click actions target the window center
- if coordinates are provided alongside a `handle`, they are treated as offsets from that window’s top-left corner
- `sendKeys` supports common chords such as `Ctrl+A`, `Shift+Tab`, `F2`, `Delete`, and `Esc`
- `typeText` types through Win32 keyboard injection, so it works against external picker/edit controls if they have focus

This is the path for OS-owned UI such as the external Windows folder picker.

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
- desktop window enumeration
- render trace capture
- event logging

This is the current regression baseline for native automation coverage.

## What I Still Cannot Fully Control

### OS-owned surfaces

These are not app-owned UI:

- the Windows folder picker launched by `Browse...`
- any system modal or external shell window

The app-owned new-project dialog is automatable. The external picker is not.

### Render-time and animation introspection

I now have:

- event sequencing
- multi-frame render trace capture
- optional per-frame screenshots

That is enough to inspect many transient behaviors around tab creation, layout refreshes, and quick native rerenders.

I still do not have:

- a high-FPS video recorder for native frames
- automatic diff summaries between successive native frames

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

I still do not have:

- element-aware drag handles inside the native shell without using coordinates
- higher-level drag semantics like “drag this tab after that tab”

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
- render trace capture
- annotated screenshots

The biggest remaining observability gap is higher-level semantic tracing for complex animations and richer drag semantics, not basic access.

# Architecture Notes

The safest build order for this app is:

1. Workspace model
2. Session model
3. Process and WSL launch layer
4. ConPTY terminal host
5. Multiplexed shell and sliding panels
6. Browser attachment surface

## Why this order

If we start with terminal rendering too early, we end up hard-coding layout and naming decisions that should belong to workspaces and sessions.

If we start with browser embedding too early, we risk designing around a web pane instead of a terminal-first shell.

The first durable layer is the workspace model:

- project or repo
- workspace
- terminal session
- browser session
- assistant thread

That gives us a stable place to support:

- Codex thread naming
- Claude Code thread naming
- project-folder style grouping
- pinned WSL distributions
- future restore and reopen behavior

## Early product ideas

- Terminal sessions should belong to a workspace, not float around as unnamed tabs.
- Assistant threads should feel like project artifacts.
- Browser panes should attach to a workspace and slide over content instead of constantly shrinking the terminal.
- WSL should be a first-class launch target, not an afterthought command template.

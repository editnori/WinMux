# Roadmap

This repo is the native Windows foundation for a terminal-first workspace app.

## Near term

1. Keep the app native on Windows with WinUI 3 and the Windows App SDK.
2. Add first-class WSL launch and session support.
3. Replace the sample scenario content with a real terminal host page.
4. Add a multiplexed layout system inspired by Neri, especially sliding panels instead of constant shrink-to-fit splits.

## Terminal experience

1. ConPTY-backed terminal sessions.
2. Named workspaces and pinned project folders.
3. Session rename, tab rename, and thread rename support for tools like Codex and Claude Code.
4. Project-oriented grouping so sessions feel closer to folders or worktrees than anonymous tabs.

## Browser experience

1. Keep browser functionality as a sibling surface, not something crammed into the terminal itself.
2. Add a Chromium-based signed-in browser pane that can attach to the same workspace or multiplexor.
3. Let browser panes slide in and out without destroying terminal context.

## Design direction

1. Native Windows feel first.
2. Terminal plus browser as coordinated surfaces.
3. Workspace management that feels calm and spatial rather than cramped.

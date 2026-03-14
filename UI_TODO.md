# UI TODO

## Shell density

- Replace the stock `TabView` visuals with a thinner custom tab strip so tabs read more like pane labels and less like app tabs.
- Make the sidebar width user-resizable, but keep the collapsed width fixed and honest.
- Add a true compact command bar for actions like new tab, duplicate tab, split pane, rename tab, and close tab.
- Reduce title-bar waste by moving window actions and shell actions into one tighter top chrome strategy.

## Session model

- Add split panes inside a tab. The reference layout earns its density from multiple live surfaces, not from decorative chrome.
- Add a session list or recent-session switcher in the sidebar instead of using the sidebar only for navigation.
- Persist tab order, active tab, working directory, and shell choice between launches.
- Let tab titles be renamed manually, then fall back to shell-derived names when the user has not set one.

## Terminal surface

- Remove the remaining gap between the tab strip and terminal content once the tab strip styling is custom.
- Add a native find bar and selection actions without re-introducing large stacked headers.
- Add per-tab shell metadata in a compact way: cwd, shell kind, and state, all inline and secondary.
- Review font metrics and line-height at smaller sizes so dense layouts still stay readable.

## Sidebar behavior

- Add hover tooltips and keyboard hints for collapsed mode.
- Add one keystroke to collapse or expand the pane.
- Let the sidebar switch between navigation mode and session mode without becoming a second dashboard.

## Settings

- Move settings from a standalone page toward inline shell preferences where it saves navigation cost.
- Keep long-form settings for low-frequency options only.
- Add theme preview swatches that stay flat and compact.

## QA

- Capture native window screenshots for closed-pane, open-pane, settings, and multi-tab states on every visual pass.
- Verify no control requires extra vertical chrome to become understandable.
- Check the shell at smaller window sizes and on longer path names so space claims stay true under pressure.

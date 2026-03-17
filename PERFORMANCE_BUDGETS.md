# Performance Budgets

This is the WinMux performance contract.

Rules:

- every user-facing feature must have an entry in `perf/feature-catalog.json`
- every automation semantic action in `MainPage.PerformAutomationAction(...)` must map to at least one catalog entry
- new features should ship with a target budget before they ship with polish
- regressions are tracked against app-side action timings from the native debugger surface, not WSL wrapper wall-clock time

Target profiles:

- `instant`: `<=10ms`
- `tight`: `<=25ms`
- `snappy`: `<=50ms`
- `standard`: `<=75ms`
- `heavy`: `<=100ms`
- `background`: `<=1500ms` async work unless a tighter feature budget says otherwise

Commands:

```bash
bun run native:perf:list
bun run native:perf:check
bun run native:perf:run
bun run native:perf:run -- --include-mutating
```

Notes:

- `native:perf:run` is conservative by default and skips mutating/destructive features
- `--include-mutating` opts into live workspace mutation for pane creation and similar actions
- fixture-required and manual features still carry budgets in the catalog even when the default suite skips them

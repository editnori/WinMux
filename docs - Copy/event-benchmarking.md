# Event Benchmarking

This is the runbook for repeated event-first annotation tests on the same note set.

The goal is not just to see whether one run works. The goal is to measure:

- which notes finalize cleanly
- how many events, gaps, and projections each note produces
- whether repeated runs on the same notes are consistent
- where the system drifts

## What Gets Compared

The consistency report compares repeated `.event.json` artifacts by semantic content, not by event IDs.

It checks:

- event signatures
- gap signatures
- projection signatures

Event signatures include:

- family
- event type
- concept id
- preferred term
- attributes
- normalization status
- anchor text
- relation targets normalized to target concept, not raw `EV*` IDs

## Step 1: Build A Fixed 10-Note Manifest

Run this once:

```bash
cd annotator
python3 -m annotation.tools.event_benchmark \
  --max-notes 10 \
  --relevance stone_relevant \
  --min-text-length 800 \
  --output data/event_benchmark_manifest.json
```

That file is the fixed note set for repeated runs.

Use a lower `--min-text-length` only if you intentionally want to include short or borderline notes.

## Step 2: Run Repeat 1

These benchmark commands use the canonical batch runner: `annotation.core.planning.swarm_orchestrator` (prepared mode by default).

Run this from top-level Codex CLI or Claude Code, not from a nested Codex session:

```bash
cd annotator
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/event_benchmark_manifest.json \
  --run-id repeat_01 \
  --model gpt-5.4 \
  --reasoning-effort medium \
  --service-tier fast \
  --timeout 600
```

This writes:

- prompts under `annotator/data/event_pilot_runs/repeat_01/prompts/` only when `--save-prompts` is passed
- summary under `annotator/data/event_pilot_runs/repeat_01/summary.json`
- artifacts under `annotator/data/event_pilot_runs/repeat_01/artifacts/`

## Step 3: Run Repeat 2 And Repeat 3

Use the same manifest and a new run id each time:

```bash
cd annotator
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/event_benchmark_manifest.json \
  --run-id repeat_02 \
  --model gpt-5.4 \
  --reasoning-effort medium \
  --service-tier fast \
  --timeout 600
```

```bash
cd annotator
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/event_benchmark_manifest.json \
  --run-id repeat_03 \
  --model gpt-5.4 \
  --reasoning-effort medium \
  --service-tier fast \
  --timeout 600
```

## Step 4: Compare Runs

```bash
cd annotator
python3 -m annotation.tools.event_consistency \
  --summary \
    data/event_pilot_runs/repeat_01/summary.json \
    data/event_pilot_runs/repeat_02/summary.json \
    data/event_pilot_runs/repeat_03/summary.json \
  --output data/event_pilot_runs/consistency_report.json
```

## How To Read The Report

Per note, look at:

- `events_identical`
- `gaps_identical`
- `projections_identical`
- `event_counts`
- `gap_counts`
- `projection_counts`
- `missing_runs`

The failure patterns mean different things:

- `events_identical = false`
  The model is extracting different event graphs on the same note.
- `gaps_identical = false`
  Normalization confidence or ontology coverage is drifting.
- `projections_identical = false`
  The resolver layer is unstable or the extracted event graph changed.
- `missing_runs` not empty
  The runner path failed before final artifact creation.

## What To Improve When Runs Drift

If event graphs drift:

- tighten family guidance
- tighten terminology tables
- add more attribute canonicalization
- add note-window tools if the agent is rereading too much text

If projections drift but events look stable:

- tighten resolver rules
- reduce broad family-level fallback projections

If runs fail before finalization:

- improve top-level runner logging
- check MCP startup and note locking
- avoid nested Codex execution

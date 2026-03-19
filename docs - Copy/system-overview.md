# System Overview — Kidney Stone Annotation Pipeline

## What Is This?

You have clinical notes from kidney stone patients. The goal is to read each note and answer ~100 clinical questions (Was there a stone? Where? How big? What procedure was done?) to build a labeled dataset.

**The key insight:** instead of asking an LLM 100 questions directly per note (inconsistent, uncited), the system first extracts *structured events* from the note, then derives the answers from those events.

```
Old approach:  Note → Answer 100 questions
New approach:  Note → Find EVENTS → Derive answers from events
```

---

## Your Role vs The System's Role

```
YOU (the clinician/annotator):
  - Run an MCP-capable client with the MCP server active
  - Open a note, review the plan, propose events for each family
  - The system handles: vocabulary normalization, evidence retrieval,
    attribute enrichment, relation validation, limited default relation synthesis, and Q&A projection

YOU do not:         Answer 100 questions per note
YOU do not:         Write regex or code
YOU do not:         Worry about exact note offsets or span bookkeeping
```

The token-efficiency redesign keeps that division of labor explicit:

- the system carries planner internals, compiled extraction guidance, controlled values, deterministic enrichment, relation checks, and projection logic
- the model focuses on exact evidence selection, event typing, preferred-term choice, and only the attributes that are not already inferable

The canonical runtime is the MCP/event-first path (`run_event_mcp.py` plus `annotation.core.planning.swarm_orchestrator`). For production batch annotation, `swarm_orchestrator` now defaults to the prepared packet path; MCP mode remains available for interactive review and rescue.

---

## Repo Layout

```
Layth_review/
│
├── annotator/
│   ├── data/
│   │   ├── consolidated_dataset.json           ← All clinical notes (source of truth)
│   │   ├── event_benchmark_manifest.json       ← Generated benchmark manifest when you build one
│   │   └── event_annotations/                  ← Where finished .event.json files land
│   │
│   ├── annotation/                             ← The runtime package
│   │   ├── core/
│   │   │   ├── event_families.py  ← The 5 topic buckets (schema + guidance)
│   │   │   ├── event_graph_normalization.py ← Anchor cleanup + merge/dedup + default relation synthesis
│   │   │   ├── event_concept_keywords.py ← Table-driven keyword hints for anchor trimming
│   │   │   └── event_projector.py ← Events → Q&A answers
│   │   │
│   │   │   ├── mcp/              ← MCP facade, state, read API, mutations, and finalization
│   │   │   ├── enrichment/       ← Deterministic enrichment dispatch + event-type-specific rules
│   │   │   ├── ontology/         ← Terminology normalization, term tables, and attribute policy
│   │   │   ├── registry/         ← Clinician-intent catalog loading + resolver inference
│   │   │   ├── planning/         ← Note planning, selection, swarm orchestration, and prompts
│   │   │   ├── infra/            ← Loader, lock, store, and session state
│   │   │   └── nlp/              ← Sectioning, span resolution, and evidence retrieval
│   │   ├── tools/                 ← Audits, manifests, and coverage reports
│   │   │   ├── question_catalog.py ← Coverage CLI entrypoint
│   │   │   ├── question_catalog_corpus.py ← Artifact-corpus counting helpers
│   │   │   └── question_catalog_render.py ← Markdown rendering helpers
│   │   └── tests/                 ← Integration-heavy suite plus direct boundary/helper coverage
│   │
│   ├── data/
│   │   └── "Variable Prioritization Summary v2.0.xlsx"  ← Initial clinician-intent seed input
│   └── run_event_mcp.py           ← Launcher shortcut
│
├── AGENTS.md                                    ← Agent operator contract
└── docs/                                        ← Architecture docs (you are here)
```

---

## The 5 Families

Each note only activates the families that apply to it:

```
┌─────────────────────┬──────────────────────────────────────────────────┐
│ Family              │ What it captures                                 │
├─────────────────────┼──────────────────────────────────────────────────┤
│ history_timeline    │ Prior stones, prior surgeries, family history,   │
│                     │ horseshoe kidney, social context                 │
├─────────────────────┼──────────────────────────────────────────────────┤
│ symptoms_imaging    │ Stone seen on CT, hydronephrosis, flank pain,    │
│                     │ stone size / location / laterality               │
├─────────────────────┼──────────────────────────────────────────────────┤
│ procedure_devices   │ Ureteroscopy, PCNL, ESWL, stent placed/removed, │
│                     │ nephrostomy tube                                 │
├─────────────────────┼──────────────────────────────────────────────────┤
│ medications         │ Tamsulosin, ketorolac, ciprofloxacin, potassium  │
│                     │ citrate, oxycodone, etc.                         │
├─────────────────────┼──────────────────────────────────────────────────┤
│ outcomes_           │ Stone passed, stone free, fever, stent removed,  │
│ complications       │ follow-up with urology, dietary recommendations  │
└─────────────────────┴──────────────────────────────────────────────────┘
```

---

## One Note, Step by Step

```
                        CLINICAL NOTE TEXT
                               │
                        ┌──────▼──────┐
                        │   PLANNER   │  annotator/annotation/core/planning/planner.py
                        │             │  - Stone relevant? (yes/maybe/no)
                        │             │  - Which families apply?
                        └──────┬──────┘
                               │
               ┌───────────────┼───────────────┐
               │               │               │
        ┌──────▼──────┐ ┌──────▼──────┐ ┌──────▼──────┐
        │ symptoms_   │ │ procedure_  │ │ medications │  ← active families only
        │ imaging     │ │ devices     │ │             │
        └──────┬──────┘ └──────┬──────┘ └──────┬──────┘
               │               │               │
               └───────────────┼───────────────┘
                               │
                   For each family, the AI agent:
                   1. Uses `prepare_active_families()` to get page-1 compact candidate handles
                   2. Uses `prepare_family(...)` to page deeper when one family needs more candidates
                   3. Calls `expand_candidates(...)` only when short context is insufficient
                   4. Proposes events in batch with `propose_events(...)`
                   5. Completes reviewed families with `complete_families(...)`
                               │
                        ┌──────▼──────┐
                        │   EVENTS    │  e.g.:
                        │             │  { type: "imaging_finding",
                        │             │    preferred_term: "kidney stone",
                        │             │    attributes: { laterality: "LEFT",
                        │             │                  size: 6, unit: "mm" } }
                        └──────┬──────┘
                               │
                        ┌──────▼──────┐
                        │  PROJECTOR  │  event_projector.py
                        │             │  Events → answers for the clinician-intent catalog
                        └──────┬──────┘
                               │
                        ┌──────▼──────┐
                        │  .event.json│  Saved artifact (events + gaps + derived projections)
                        └─────────────┘
```

---

## What Is an Event?

One structured fact extracted from the note text:

```json
{
  "event_id": "EV3",
  "family": "symptoms_imaging",
  "event_type": "imaging_finding",
  "preferred_term": "kidney stone",
  "attributes": {
    "laterality": "LEFT",
    "location": "lower pole calyx",
    "size": 6,
    "unit": "mm",
    "status": "nonobstructing"
  },
  "evidence_set": {
    "anchor_mode": "single_span",
    "anchors": [
      {
        "text": "6 mm nonobstructing calculus in the left lower pole"
      }
    ]
  }
}
```

`preferred_term` is canonical when the terminology table recognizes the concept. If no canonical match exists, the runtime can preserve a caller-supplied custom term or mint a provisional concept identity until the ontology catches up.

---

## The Planner

Before extraction, the planner reads the full note and decides:

1. **Is this stone-relevant?** → `stone_relevant` / `possibly_relevant` / `irrelevant`
2. **Which families are worth running?**

In batch mode, `plan_note(detail="compact")` now returns only the information the model needs immediately:

- `note_type`
- `relevance_label`
- `active_families`
- `stone_signals`
- `incidental_stone_finding`
- `family_routes`
- `outline_sections` when outline inclusion is requested or enabled by batch mode

The full planner internals are still available with `detail="full"`, but they are no longer pushed into the model context by default.

## The Extraction Brief

`get_family_bundle(detail="compact")` no longer ships raw registry rows by default. Instead it returns a compiled extraction brief per family for interactive/manual review:

- event types
- preferred terms
- controlled attributes and values
- `coverage_goals`
- `must_capture`
- `auto_inferred`

This keeps question-coverage guidance in the system while reducing LLM clutter.

## Evidence Retrieval

`prepare_active_families()` is the canonical page-1 batch read path. `prepare_family(family, cursor=None, limit=None)` pages one family deeper when the first candidate set is insufficient. They return match-centered evidence units:

- `candidate_id`
- `span_ids`
- `section`
- `quote`
- `context`

`expand_candidates(candidate_ids=[...], detail=...)` is the only text-widening path in batch mode. `plan_note(..., include_outline=None)` can include outline sections when requested or mode-enabled; `get_family_bundle()` and `get_evidence_candidates()` remain available for interactive/manual workflows, but they are disabled in outline-first batch runs. The canonical batch runner uses `open_and_plan(...)`, routed compact candidate handles, batched event proposals, and batched family completion.

Compact candidates are bounded around the matched evidence rather than echoing long pseudo-lines. `detail="full"` remains available for debugging.

It uses keyword signals with context awareness:
- "nephrolithiasis" in PMH section → **suppressed** (it's history, not the current visit)
- "nephrolithiasis" in HPI → **kept**
- "no kidney stones" → **suppressed** (negated)
- "denies fever" → **suppressed** (negated)

## How to Run

For runnable commands, use [annotation-runbook.md](annotation-runbook.md). Keep this document focused on architecture and invariants rather than operational command duplication.

---

## Key Files Reference

| File | Purpose |
|---|---|
| `annotator/annotation/core/planning/planner.py` | Relevance classification + family selection |
| `annotator/annotation/core/event_families.py` | Event schema and family guidance |
| `annotator/annotation/core/ontology/terminology.py` | Raw text → canonical term mappings |
| `annotator/annotation/core/nlp/section_detector.py` | PMH / HPI / Assessment section boundaries |
| `annotator/annotation/core/nlp/evidence_retriever.py` | Sentence-level relevance scoring |
| `annotator/annotation/core/mcp/read_api.py` | MCP read surfaces: open/plan, routing, candidate prep, and context reads |
| `annotator/annotation/core/mcp/mutations.py` | MCP mutation surfaces: propose/revise events, gaps, and family completion |
| `annotator/annotation/core/mcp/finalize.py` | Final artifact write, projection, lock release, and session teardown |
| `annotator/annotation/core/event_projector.py` | Events → Q&A answer projection |
| `annotator/annotation/core/infra/event_store.py` | Load / save `.event.json` artifacts |
| `annotator/annotation/core/registry/loader.py` | Imports clinician-intent entries from the seed workbook |
| `annotator/annotation/tools/event_corpus_audit.py` | Batch planner audit over a fixed note corpus |

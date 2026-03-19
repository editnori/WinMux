# Annotation Runbook — Event-First Pipeline

How to start and run a full annotation session from scratch.

---

## Prerequisites

Start the MCP server in one terminal, then open an MCP-capable agent client in another.
The `.mcp.json` at the repo root already points Codex or Claude Code to the server.
Operationally:
- use Codex for production batch annotation
- production batch runs now default to `--mode prepared`
- use MCP mode for interactive review, rescue, and note-level debugging
- use Claude/Sonnet/Opus for audit, spot checks, and disagreement review

```bash
# Terminal 1 — start the MCP server
python3 annotator/run_event_mcp.py

# Terminal 2 — open your MCP-capable client (MCP connects automatically)
# Production default:
codex
```

Runtime modules referenced below now live under `annotator/annotation/core/`.

---

## The Flow — What Happens to Each Note

```
RAW NOTE TEXT
     │
     ▼
┌─────────────┐
│   PLANNER   │  annotator/annotation/core/planning/planner.py
│             │  Reads the full note.
│             │  Outputs:
│             │    relevance_label:  stone_relevant / possibly_relevant / irrelevant
│             │    active_families:  which of the 5 topic buckets matter
└──────┬──────┘
       │
       │  irrelevant → finalize_note() and move on in the default annotate flow
       │               (relevance-review/manual workflows may still reopen and rescue)
       │  stone_relevant → proceed
       │
       ▼
For each active family:

┌──────────────────────────────────────────────────────────────┐
│  EVIDENCE RETRIEVER  (annotator/annotation/core/nlp/evidence_retriever.py)        │
│                                                              │
│  Builds an inverted index of the note text.                  │
│  Scores sentence windows by keyword overlap with the family. │
│  Multi-word phrases get a bonus score.                       │
│  Returns compact candidate handles centered on routed spans.  │
└─────────────────────┬────────────────────────────────────────┘
                      │
                      ▼
┌──────────────────────────────────────────────────────────────┐
│  ANNOTATION AGENT (Codex in production; Claude for audit)    │
│                                                              │
│  Reads the candidate snippets.                               │
│  Decides what events to propose.                             │
│  Calls propose_events() or propose_event() with:             │
│    family          e.g. "symptoms_imaging"                   │
│    event_type      e.g. "imaging_finding"                    │
│    preferred_term  controlled vocabulary from annotator/annotation/core/ontology/terminology.py │
│    attributes      laterality, size, status, etc.            │
│    evidence_refs   candidate/span handles when possible      │
│    evidence_quotes verbatim substrings only when needed      │
└─────────────────────┬────────────────────────────────────────┘
                      │
                      ▼
┌──────────────────────────────────────────────────────────────┐
│  VALIDATOR + ENRICHER  (annotator/annotation/core/mcp/ + enrichment/ + normalization) │
│                                                              │
│  For each evidence_quote:                                    │
│    - Finds its exact char position in the note text          │
│    - Records start, end, section label (HPI / Assessment…)   │
│    - Rejects the event if the quote cannot be located        │
│                                                              │
│  Terminology lookup (annotator/annotation/core/ontology/terminology.py): │
│    "calculus" → "kidney stone"  (CANONICAL)                  │
│    procedure modifiers stay in raw_mention / attributes      │
│    unless they already map to an allowed canonical term      │
│                                                              │
│  Attribute enrichment from anchor text:                      │
│    sees "CT" near anchor  → adds attribution: "CT"           │
│    sees "left" near anchor → confirms laterality: "LEFT"     │
│    sees "nonobstructing"  → status: "nonobstructing"         │
└─────────────────────┬────────────────────────────────────────┘
                      │
                      ▼
              EVENT GRAPH
              (validated events, each with evidence_set.anchors
               pointing to char offsets in the note text)
                      │
                      ▼
┌──────────────────────────────────────────────────────────────┐
│  PROJECTOR  (event_projector.py)                             │
│                                                              │
│  Reads the clinician-intent catalog.                         │
│  For each clinician question scoped to this note type:       │
│    - Finds relevant events                                   │
│    - Extracts the answer from event attributes               │
│    - Labels it answered or unanswered                        │
│                                                              │
│  e.g.:                                                       │
│    "is_hydronephrosis_present"    → True    (from EV3)       │
│    "what_medication_was_given"    → ["morphine","ondansetron"]│
│    "what_was_the_laser_setting_power" → null (note silent)   │
└─────────────────────┬────────────────────────────────────────┘
                      │
                      ▼
         .event.json artifact  (saved to disk)
         ├── header          note_id, patient_id, timestamp
         ├── note_plan        relevance + active families + candidates
         ├── events[]         the extracted event graph
         ├── projections[]    clinician-facing derived answers
         ├── family_reviews[] per-family summaries
         └── gaps[]           unmapped terms (if submitted)
```

Artifact path: `annotator/data/event_annotations/{PID}/{NOTE_TYPE}/{NOTE_ID}.event.json`

---

## Per-Note Workflow (tool call sequence)

This block describes the canonical event-first tool flow. Production batch annotation now defaults to the prepared packet path inside `swarm_orchestrator`; for manual note debugging, you can still use the lower-level MCP tools directly.

```
1. open_and_plan(patient_id, note_id, detail="compact", include_outline=None)
   → check relevance_label and active_families
   → use family_routes as routing hints, not as evidence
   → if irrelevant: finalize_note()

2. prepare_active_families()
   → start from the returned page-1 compact candidate handles for all active families
3. prepare_family(family, cursor=...)
   → page deeper for one family before widening context
4. expand_candidates(candidate_ids=[...], detail="sentence")
   → use only when the short quote/context is insufficient
   → expand one candidate at a time; escalate to paragraph/full only when needed
5. propose_events(events=[...])
   → batch event proposals whenever practical
   → only send attributes the note makes explicit and the system cannot already infer
6. complete_families(families=[...])
   → batch reviewed-family updates whenever practical

7. finalize_note()
   → writes the artifact and releases the note lock
```

---

## The 5 Families

| Family | What it captures |
|---|---|
| `history_timeline` | Prior stones, prior procedures, family history, social context |
| `symptoms_imaging` | Stone findings on CT/US, hydronephrosis, symptoms |
| `procedure_devices` | URS, PCNL, ESWL, stents, nephrostomy tubes |
| `medications` | Tamsulosin, ketorolac, antibiotics, stone prevention meds |
| `outcomes_complications` | Stone passage, AKI, fever, follow-up recommendations |

---

## What "preferred_term" Means

Always use controlled vocabulary — the terminology dictionary maps raw clinical text to canonical terms:

| Raw text | preferred_term |
|---|---|
| calculus, renal stone, nephrolith | `kidney stone` |
| ureteral calculus | `ureteral stone` |
| Flomax | `tamsulosin` |
| Toradol | `ketorolac` |
| JJ stent | `ureteral stent` |
| neph tube | `nephrostomy tube` |
| holmium laser use during ureteroscopy | keep `preferred_term` on the canonical procedure and preserve the technique detail in attributes or `raw_mention` unless the runtime exposes a distinct canonical procedure term |

CANONICAL = in the dictionary. PROVISIONAL = accepted but needs to be added.

---

## Evidence Quote Rules

- Quotes must be **verbatim substrings** of the note text.
- If a quote spans two sentences (period + capital), it will trigger `CROSS_SENTENCE_REQUIRES_MULTIPLE_QUOTES`.
  Fix: pass the two sentences as separate strings in the `evidence_quotes` array, or use a shorter quote that stays within one sentence.
- The validator resolves each quote to char offsets — if it can't find the text, the event is rejected.

---

## Medication Selectivity Rule

Only extract medications **clinically relevant to stone management**:
- Pain control (morphine, ketorolac, oxycodone)
- Alpha-blockers for MET (tamsulosin)
- Antibiotics for stone-related infection (UTI, pyelonephritis, urosepsis)
- Stone prevention (potassium citrate, allopurinol, hydrochlorothiazide)
- Antiemetics in stone context (ondansetron, metoclopramide)

Skip: warfarin/Lovenox (DVT), antihypertensives, diabetes meds, etc.

---

## Key Decisions for Borderline Notes

**Incidental stones** (imaging ordered for something else):
→ Still create imaging_finding events, set `clinical_significance: "incidental"`.

**PR (possibly_relevant) notes**:
→ Open and plan. If the stone signal is negated ("denies hematuria"), or is clearly from non-stone cause (rib fracture flank pain, bladder cancer hematuria) → complete all families empty and finalize.

**Prior history in PSH only**:
→ If stone history appears only in past surgical history and the current visit has no stone-active content → do not extract history_fact events. The PSH section alone is not enough.

**Same patient, multiple notes on same admission**:
→ Annotate each note independently based on what is explicitly in that note. Do not copy events from companion notes.

---

## Running on the Full Corpus

This runbook uses the canonical batch runner: `annotation.core.planning.swarm_orchestrator` (prepared mode by default).

```bash
# Build a fixed benchmark manifest first if you do not already have one
cd annotator
python3 -m annotation.tools.event_benchmark \
  --max-notes 10 \
  --relevance stone_relevant \
  --min-text-length 800 \
  --output data/event_benchmark_manifest.json

# Check how many notes remain in that manifest
python3 -m annotation.tools.event_corpus_audit --manifest data/event_benchmark_manifest.json

# Run the production batch path (Codex)
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/event_benchmark_manifest.json \
  --backend codex \
  --model gpt-5.4 \
  --concurrency 15

# Claude variants are useful as audit/review lanes, not the default production backend
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/event_benchmark_manifest.json \
  --backend claude \
  --model claude-sonnet-4-6 \
  --concurrency 5

# For a larger corpus run, point `annotation.core.planning.swarm_orchestrator` at an explicit manifest
python3 -m annotation.core.planning.swarm_orchestrator \
  --manifest data/another_manifest.json \
  --backend codex \
  --model gpt-5.4 \
  --concurrency 15
```

---

## Verifying an Artifact

To trace any answer back to the note text:

```
projection.registry_id → "is_hydronephrosis_present"
projection.supporting_event_ids → ["EV3"]
event EV3 → evidence_set.anchors[0].text → "There is moderate left-sided hydronephrosis..."
event EV3 → evidence_set.anchors[0].start → 9810  (char offset in note)
```

---

## Current Open Issues

Live open issues should come from generated audit output rather than this runbook.

Use:
- `annotation.tools.event_quality_gate`
- `annotation.tools.event_efficiency_report`
- `annotation.tools.event_consistency`
- `annotation.tools.event_corpus_audit`

to inspect the current state of planner coverage, relation yield, projection drift, and runtime cost.

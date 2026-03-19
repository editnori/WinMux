# Event-First Annotation Pipeline Walkthrough

## The 5 Stages

```
Raw Note Text (e.g. 5,000 chars of clinical note)
       │
       ▼
┌──────────────────────────────────────────────────────────┐
│ Stage 1: PLANNER (pure regex, no AI)                     │
│   "Is this note about kidney stones?"                    │
│   Input:  raw note text                                  │
│   Output: relevance label + which families to activate   │
│   Code:   annotator/annotation/core/planning/planner.py → build_note_plan()   │
└──────────────────────┬───────────────────────────────────┘
                       │ stone_relevant → proceed
                       ▼
┌──────────────────────────────────────────────────────────┐
│ Stage 2: EVIDENCE RETRIEVAL (lexical index, no AI)       │
│   "Where in the note should the AI look first?"          │
│   Input:  family keywords + note text                    │
│   Output: compact candidate handles for routed families  │
│   Code:   annotator/annotation/core/nlp/evidence_retriever.py → NoteEvidenceRetriever  │
└──────────────────────┬───────────────────────────────────┘
                       │ candidate handles (hints, not limits)
                       ▼
┌──────────────────────────────────────────────────────────┐
│ Stage 3: EVENT PROPOSAL (AI model — the stitcher)        │
│   "What clinical events exist in this note?"             │
│   Input:  routed candidates + targeted expansions + family schema    │
│   Output: proposed events with multi-span evidence       │
│   Code:   canonical batch path uses MCP tools            │
│                                                          │
│   In outline-first batch mode, the AI starts from        │
│   compact candidate handles and requests wider context   │
│   only when needed. Interactive/manual workflows can     │
│   still inspect broader note text and debug tools.       │
└──────────────────────┬───────────────────────────────────┘
                       │ proposed events
                       ▼
┌──────────────────────────────────────────────────────────┐
│ Stage 4: VALIDATION & ENRICHMENT (deterministic)         │
│   "Is the AI's evidence real? Can we add attributes?"    │
│   Input:  proposed event + raw note text                 │
│   Output: accepted event with anchors + enriched attrs   │
│   Code:   canonical batch path uses `propose_events()` first, with `propose_event()` as fallback │
└──────────────────────┬───────────────────────────────────┘
                       │ validated events
                       ▼
┌──────────────────────────────────────────────────────────┐
│ Stage 5: PROJECTION (deterministic)                      │
│   "What clinician questions does this event answer?"     │
│   Input:  all events + clinician-intent catalog entries  │
│   Output: answered questions with supporting event IDs   │
│   Code:   event_projector.py → project_note_answers()    │
└──────────────────────────────────────────────────────────┘
```

---

## Detailed Stage Breakdown

### Stage 1: Planner

`annotator/annotation/core/planning/planner.py` scans the raw note with ~30 regex patterns:

```
nephrolithiasis, kidney stone, renal stone, ureteral stone,
calculus, hydronephrosis, ureteroscopy, lithotripsy,
ureteral stent, nephrostomy, flank pain, hematuria, ...
```

If any hit, the note is marked `stone_relevant` or `possibly_relevant`. The planner also decides which of the 5 families to activate based on keyword overlap:

- `history_timeline` — prior stone history, family history
- `symptoms_imaging` — stones, hydronephrosis, symptoms
- `procedure_devices` — ureteroscopy, stent, PCNL
- `medications` — tamsulosin, ketorolac, opioids
- `outcomes_complications` — stone passage, adverse effects

If 0 patterns match → `irrelevant` → the default annotate flow stops early with no family extraction. Relevance-review and manual rescue workflows can still reopen these notes if later review finds supported stone content.

**Accuracy risk**: False negatives. If a note describes a stone episode using only unusual language that the regex doesn't cover, the planner can mark it irrelevant and the default annotate flow will stop early. See "Known Limitations" below for the rescue caveat.

### Stage 2: Evidence Retrieval

For each active family, `NoteEvidenceRetriever` builds an inverted index (token → line numbers) and scores lines by keyword density. The canonical batch path packages the best hits as compact candidate handles via `prepare_family(...)` or `prepare_active_families()`.

This is a **hint system**, not a constraint. The model starts from the routed candidate handles and can ask for wider context with `expand_candidates(...)` when the short quote is insufficient. In interactive/manual MCP use, `get_evidence_candidates()` and `note://current` still exist for debugging, but they are not the default batch path.

The live MCP resources are:
- `note://current`
  Raw current-note text for interactive/manual review. Disabled in outline-first batch mode.
- `progress://patient/{patient_id}`
  Patient progress summary showing completed vs remaining notes and artifact status.

### Stage 3: Event Proposal (the AI)

The LLM (GPT-5.4 or Claude) reads:
1. The **routing payload** from `open_and_plan(...)` or `plan_note(...)`
2. The **compact candidate handles** from `prepare_family(...)` or `prepare_active_families()`
3. Any **targeted expansions** requested with `expand_candidates(...)`
4. The **family schema / extraction brief** from the system prompt and, for interactive/manual review, `get_family_bundle(...)`

The LLM then proposes events by calling `propose_events()` when practical, with `propose_event()` as a fallback for one-off revisions or sparse cases:
- `event_type` — chosen from the family schema (e.g., `imaging_finding`, `symptom_presentation`)
- `preferred_term` — a clinical concept name (e.g., "hydronephrosis", "ureteral stone")
- `evidence_quotes` — exact text copied from the note (can be from anywhere)
- `attributes` — structured key-value pairs (laterality, size, status, etc.)
- `relations` — explicit links to other events (performed_for, decompresses, caused_by)

The AI chooses the values. The schema tells it which attribute keys exist (e.g., `laterality`, `location`, `severity`) but most values are free-text — the AI reads the note and fills them in. Some values are then normalized by Stage 4.

**If the ontology doesn't have the right option**: The AI calls `submit_gap()` instead. This records the raw concept, the evidence, and the family, and flags it for human review. The gap says "I found something real in the note, but I don't know how to categorize it." Gaps are saved in the artifact alongside events.

### Stage 4: Validation & Enrichment

When `propose_event()` is called, deterministic code:

1. **Anchor resolution** — finds each evidence quote in the raw note text. If a quote doesn't exist verbatim → REJECTED. No hallucinated evidence allowed.
2. **Section detection** — labels each anchor with its note section (HPI, ASSESSMENT, EXAM, IMAGING, etc.)
3. **Terminology normalization** — maps the AI's `preferred_term` to canonical form (e.g., "renal calculus" → "kidney stone", "bilateral hydronephrosis" → "hydronephrosis")
4. **Attribute enrichment** — regex patterns extract additional attributes from anchor text that the AI might have missed:
   - laterality (LEFT/RIGHT/BILATERAL)
   - device status (in_situ/removed/exchanged)
   - medication frequency (QID/BID/daily/PRN)
   - imaging attribution (CT/ultrasound/X-ray)
   - adverse effect timing (intra-operative/post-operative)

### Stage 5: Projection

After all families are reviewed and `finalize_note()` is called, `event_projector.py` maps events to clinician questions using resolver rules:

- **event_presence**: "Does an event of type X exist?" → yes/no
- **event_concepts**: "What concepts of type X were found?" → list of preferred_terms
- **event_attribute_values**: "What are the values of attribute Y on events of type X?" → list of values

Each resolver filters events by family, event_type, concept keywords, and attribute values. NEGATED and HISTORICAL events are excluded.

---

## Concrete Example: 3-Span Stitching

A discharge summary contains:

```
Position  381 [Narrative]:  "Still experiencing symptoms from her obstructive stone."
Position 1409 [Plan]:       "Nausea and vomiting"
Position 2925 [Exam]:       "tender to palpation in the left lower quadrant"
```

No single span answers the clinician question "Was this patient symptomatic from the stone?"

- Span 1 says symptoms are FROM the stone, but doesn't name them
- Span 2 names symptoms, but doesn't link them to the stone
- Span 3 gives laterality and exam finding, but in isolation is just an abdominal exam

The AI reads the full note, recognizes these three spans describe the same clinical picture, and proposes one event:

```
propose_event(
    family = "symptoms_imaging",
    event_type = "symptom_presentation",
    preferred_term = "stone-related symptomatic presentation",
    evidence_quotes = [
        "Still experiencing symptoms from her obstructive stone.",
        "Nausea and vomiting",
        "tender to palpation in the left lower quadrant"
    ],
    attributes = {
        "symptoms": ["ABDOMINAL PAIN", "NAUSEA AND VOMITING"],
        "laterality": "LEFT",
        "attribution": "obstructive stone"
    }
)
```

The projector then derives:
- `was_the_patient_symptomatic_from_this_stone` → **True** (event_presence resolver)
- `what_were_the_patient_s_symptoms` → **["ABDOMINAL PAIN", "NAUSEA AND VOMITING"]** (event_attribute_values resolver)

---

## Known Limitations

### 1. Planner False Negatives (irrelevant notes that had useful content)

If the planner marks a note `irrelevant`, the default annotate flow stops before specialist-family extraction. The planner is intentionally high-recall (many regex patterns), but it can miss:

- Notes that describe stones using only indirect language ("the patient's prior intervention for the obstructing mass")
- Notes where stone content is buried in a problem list that gets suppressed by the section-reset filter

Mitigation: The planner has been tuned across the corpus, but it is still a lexical gate. Relevance-review and manual rescue remain part of the operating model. For current corpus metrics, use the generated benchmark and audit outputs instead of this architecture walkthrough.

### 2. Evidence Retrieval Is Hints, Not Limits

The evidence retrieval candidates are a convenience for the AI — "here's where keywords matched." In the canonical batch path the model starts from these compact handles, then asks for wider local context only when needed.

In the 3-span example above, Span 3 ("tender to palpation in the left lower quadrant") has no stone keywords at all. The retriever would not rank it highly on its own. The model can still recover it by expanding nearby candidate context and linking multiple anchors into one event.

So: the evidence retriever is a routing aid, not the final truth layer. It should focus the model, but final acceptance still depends on evidence anchors that resolve against the note.

### 3. Ontology Gaps

The family schemas define which `event_type` values exist and which `attribute` keys are recognized. If the note contains a concept that doesn't fit any event type, the AI has two options:

1. **Stretch an existing type** — e.g., use `outcome_event` for an unusual post-operative finding
2. **Submit a gap** — call `submit_gap(family, raw_concept, evidence_quotes)` to flag it

Gaps are saved in the artifact and can be reviewed later to expand the ontology. Use the generated audit outputs for current gap-rate snapshots rather than relying on a hard-coded example run here.

### 4. Attribute Values Are Mostly Free-Text

The schema tells the AI which attribute keys exist (e.g., `laterality`, `location`, `severity`), but most values are not constrained to an enum. The AI writes what it reads from the note.

Stage 4 enrichment normalizes some values deterministically (e.g., "right" → "RIGHT", "every 4 hours" → "q4h", "unchanged" → "stable"), but many attribute values remain as the AI wrote them.

### 5. Relations Are Still Sparse

The AI is prompted to create relations, and the runtime validates declared relations plus synthesizes a limited default set. Relation yield is still lower than event yield in practice, so relation-heavy notes should be reviewed explicitly using the current audit outputs rather than relying on a fixed historical snapshot.

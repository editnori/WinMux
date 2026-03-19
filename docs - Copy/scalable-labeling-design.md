# Evidence-First Redesign For Clinician Questions Over Notes

## What This Changes

The old system treated the ontology questionnaire as the main object.

The new system treats the clinical fact as the main object.

That changes the job in a useful way:

- clinicians still define what matters
- notes still provide the evidence
- the LLM extracts note-grounded events
- deterministic code turns those events into answers

This is a clean break. The current ontology is reference material, not the design boundary.

## Why The Change Is Necessary

The spreadsheet captured what clinicians want to know. It did not give you a clean extraction model.

Once you started labeling notes, three things became obvious:

- evidence is often a span, not a token
- one answer can depend on multiple spans, sometimes across sentences
- the same clinical fact keeps getting repeated across overlapping questions

That is why the current question-first design feels crowded. It asks the model to do two jobs at once:

- understand the note
- navigate a large question system with skip logic, mapping rules, and fallback categories

That is wasted context.

## Before And After

Take a note like this:

> Patient with nephrolithiasis s/p bilateral ureteroscopy and left JJ stent placement today, presenting with left flank pain, abdominal pain and incontinence. She has had hematuria and dysuria. In PACU today she was given fentanyl for pain and shortly thereafter developed hives.

### Before

The question-first system asks several overlapping questions:

- was the patient symptomatic
- what symptoms were present
- what procedure happened
- was a stent left
- what medication was given
- what side effect happened

The result is a fragmented output:

- one symptom cluster becomes several question answers
- the stent becomes a separate answer from the procedure
- the medication becomes separate from the adverse effect
- the model keeps rereading the same evidence for different questions

### After

The event-first system extracts the note once and stores a compact event graph.

`symptom_presentation`

```json
{
  "event_type": "symptom_presentation",
  "preferred_term": "stone-related symptomatic presentation",
  "attributes": {
    "symptoms": [
      "LEFT FLANK PAIN",
      "ABDOMINAL PAIN",
      "HEMATURIA",
      "DYSURIA",
      "INCONTINENCE"
    ],
    "laterality": "LEFT",
    "clinical_context": "emergency presentation",
    "attribution": "stone-related"
  },
  "evidence_set": {
    "anchor_mode": "cross_sentence",
    "anchors": [
      "presenting with left flank pain, abdominal pain and incontinence",
      "She has had hematuria and dysuria"
    ]
  }
}
```

`procedure_event`

```json
{
  "event_type": "procedure_event",
  "preferred_term": "ureteroscopy",
  "attributes": {
    "laterality": "BILATERAL",
    "timing": "same day prior to presentation"
  },
  "evidence_set": {
    "anchor_mode": "single_span",
    "anchors": [
      "s/p bilateral ureteroscopy"
    ]
  }
}
```

`device_event`

```json
{
  "event_type": "device_event",
  "preferred_term": "ureteral stent",
  "attributes": {
    "device_type": "JJ stent",
    "laterality": "LEFT",
    "status": "placed"
  },
  "evidence_set": {
    "anchor_mode": "single_span",
    "anchors": [
      "left JJ stent placement today"
    ]
  }
}
```

`medication_exposure`

```json
{
  "event_type": "medication_exposure",
  "preferred_term": "fentanyl",
  "attributes": {
    "drug_class": "opioid analgesic",
    "indication": "pain",
    "context": "PACU"
  },
  "evidence_set": {
    "anchor_mode": "single_span",
    "anchors": [
      "In PACU today she was given fentanyl for pain"
    ]
  }
}
```

`adverse_effect`

```json
{
  "event_type": "adverse_effect",
  "preferred_term": "hives",
  "attributes": {
    "reaction_type": "urticarial reaction",
    "assertion": "suspected medication-related"
  },
  "relations": [
    {
      "relation_type": "caused_by",
      "target_event_id": "EV4"
    }
  ],
  "evidence_set": {
    "anchor_mode": "single_span",
    "anchors": [
      "shortly thereafter developed hives"
    ]
  }
}
```

From there, the resolver layer can answer clinician questions without asking the LLM to re-extract the same story:

- symptomatic from stone: `yes`
- symptoms present: `left flank pain`, `abdominal pain`, `hematuria`, `dysuria`, `incontinence`
- procedure: `bilateral ureteroscopy`
- post-op device: `left JJ stent`
- medication: `fentanyl`
- side effect: `hives`, linked to fentanyl by the event graph

That is cleaner because the note was interpreted once, not sliced into overlapping question fragments.

## The New Three-Layer Model

### 1. Clinician Registry

The spreadsheet becomes a registry of clinician intent.

Each registry row should capture:

- `registry_id`
- clinician wording
- priority fields from the spreadsheet
- `clinical_intent`
- `family`
- `source_class`: `note`, `hybrid`, `structured_data`
- `answer_scope`: `note`, `encounter`, `patient_timeline`, `index_relative`
- `answer_shape`: `categorical`, `numeric`, `list`, `relation`, `timeline_fact`
- `implemented_status`: internal wiring status used for registry/projector bookkeeping
- `review_priority`

This layer answers one question: what do clinicians want to ask?

It does not decide how the LLM should label the note.

### 2. Event Extraction

This becomes the internal truth.

Core objects:

- `EvidenceAnchor`
  One exact note-backed anchor with start, end, text, and section.
- `EvidenceSet`
  One or more anchors with `single_span`, `multi_span`, or `cross_sentence`.
- `ClinicalEvent`
  A note-grounded fact with type, `concept_id`, `preferred_term`, attributes, provenance, and evidence.
- `Relation`
  A typed edge between events.
- `GapRecord`
  An evidence-backed record for a concept that does not fit cleanly yet.

This layer answers one question: what does the note actually say?

### Terminology Normalization

This is a separate layer inside the event model.

- evidence anchors keep the literal chart wording
- `raw_mention` stores the first normalized surface mention when we want it on the event
- `preferred_term` stores the term we want to use consistently
- `concept_id` stores the stable identity used across notes

Example from the real ED note:

- note wording: `developed hives`
- preferred term: `hives`
- concept id: `outcomes_complications.adverse_effect.hives`

That split matters because the note can say one thing, the preferred label can say another, and the concept identity stays stable either way.

### Canonicalization Formula

The system now treats canonicalization as a deterministic policy:

1. Clean spacing and punctuation from the proposed term.
2. Look it up in the event-type-specific alias table.
3. If the alias table has a match, store the controlled `preferred_term` and build a stable `concept_id`.
4. If there is no match but the caller supplied an explicit `concept_id`, keep it and mark the event as `CUSTOM`.
5. If there is no match and no explicit `concept_id`, derive a fallback `concept_id` from the cleaned term and mark it `PROVISIONAL`.

The same pattern applies to controlled attribute values. Right now that includes things like:

- symptom lists
- device types
- medication administration context
- medication class
- adverse-effect reaction type

This gives you a simple formula:

- raw chart wording stays in evidence
- known aliases collapse into one preferred term
- unknown terms remain usable, but visibly provisional

### 3. Projection Rules

Projection happens after extraction.

Resolvers translate events into clinician-facing answers:

- note-scoped questions use note events
- patient-history questions use timeline facts built from dated events
- index-relative questions use event dates plus index-date logic

This layer answers one question: how do the stored facts answer the clinician ask?

## The LLM-Native Labeling Model

The right mental model is not “LLM fills out a long form.”

The right mental model is “LLM works like a labeling operator inside a guarded UI.”

In a human labeling tool:

- you highlight text that exists
- you assign a label from a known schema
- you link labels when needed
- the tool stores exact positions and rejects invalid actions

The LLM version should behave the same way.

### Tool Surface

- `open_note(patient_id, note_id)`
  Opens and locks the note.
- `open_and_plan(patient_id, note_id, detail="compact", include_outline=None)`
  Canonical batch entrypoint that opens the note and returns the initial routing payload in one call.
- `plan_note(detail="compact" | "full")`
  Decides relevance and active families; compact mode returns only the routing payload the model needs.
- `prepare_family(family, cursor=None, limit=None)`
  Returns compact candidate handles for one routed or review-activated family.
- `prepare_active_families(limit_per_family=None)`
  Returns page-1 compact candidate handles for all active families in one call.
- `expand_candidates(candidate_ids=[...], detail="sentence" | "paragraph" | "full")`
  Widens compact candidate handles only when short context is insufficient.
- `get_family_bundle(family, detail="compact" | "full")`
  Returns a compiled extraction brief in compact mode and the full verbose schema/policy payload in full mode. Use primarily for interactive/manual review.
- `get_evidence_candidates(..., detail="compact" | "full")`
  Returns bounded quote/context evidence units in compact mode and richer debug metadata in full mode. Use primarily for interactive/manual review.
- `propose_event(...)`
  Proposes event type, concept, attributes, and evidence quotes.
- `propose_events(events=[...])`
  Batch mutation helper that submits multiple event proposals in one call.
- `revise_event(...)`
  Patches an accepted event.
- `submit_gap(...)`
  Records an unmapped concept with evidence.
- `get_timeline_context(...)`
  Returns related note context for later longitudinal logic.
- `complete_family(family, summary="...")`
  Marks one reviewed family complete so finalization can proceed deterministically.
- `complete_families(families=[...])`
  Batch mutation helper that marks multiple families reviewed in one call.
- `finalize_note()`
  Persists the event artifact and writes derived projections.

### What The Model Does

The model should only do the semantic work:

- read the note
- pick the clinically meaningful fact
- select evidence quotes
- assign event type and the attributes that are not already inferable
- link related events when the note supports the relation

### What The System Does

The system should do the deterministic work:

- resolve quotes to exact note positions
- reject anchors that do not map to note text
- enforce family and event schemas
- enforce relation integrity
- carry compiled extraction guidance, coverage goals, and controlled values
- auto-infer supported attributes from evidence when deterministic enrichment can do it reliably
- persist accepted events
- derive answers from stored events
- queue unmapped concepts for review

This is how you keep interpretability and consistency without forcing the LLM through a questionnaire maze.

The same logic applies to normalization:

- the model points at the wording that exists
- the model chooses the normalized preferred term
- the system preserves canonical concepts without requiring a synthetic `concept_id`, keeps caller-supplied custom IDs, and only mints provisional IDs when normalization remains unsettled
- gap records catch concepts whose preferred normalization is still unsettled

## Why This Uses Fewer Tokens

This design is more token-efficient by construction.

The model no longer receives every question for every note. Instead:

- `plan_note()` filters out irrelevant notes
- only active families are loaded
- batch mode starts with `prepare_active_families()` and pages deeper with `prepare_family(...)`
- evidence retrieval returns short candidate handles, with `expand_candidates(...)` only when needed
- one extracted event can answer several clinician questions later

The savings come from removing repeated question handling, not from squeezing prompts harder.

A symptom cluster should be extracted once and reused. A medication event should be extracted once and reused. A complication linked to a medication should be represented once and reused.

That is cheaper than asking the model to restate the same fact in several question formats.

## Workflow

FLOW: clean-break note annotation

Entrypoint:
- one note is opened for annotation

Inputs:
- note text
- clinician-intent catalog
- family specs
- evidence candidate retrieval

Happy path:
1. Open and lock one note.
2. Plan relevance and active families.
3. Skip irrelevant notes early.
4. Start from `prepare_active_families()` page-1 candidate handles.
5. Page one family deeper with `prepare_family(...)` and widen with `expand_candidates(...)` only when needed.
6. Specialists propose evidence-backed events.
7. Adjudicate duplicate or conflicting events.
8. Record gaps for unmapped concepts.
9. Mark every active family complete.
10. Finalize the note and derive projections.

Outputs:
- note plan
- event artifact
- derived projection records
- gap records

Active artifact schema:
- `annotator/annotation/core/event_artifact.schema.json`

Side effects:
- note lock write
- event artifact write
- review queue growth through gap records

Failure modes:
- invalid anchor quote -> reject event
- family not active -> reject event
- invalid relation target -> reject event
- unresolved concept -> gap record
- conflicting specialist outputs -> adjudication or review

Observability:
- event artifact contents
- tool return values
- test coverage for planning, anchors, projections, and finalize behavior

## Agent Roles

Use specialist-family agents inside each note.

- `Planner`
  Chooses relevance and active families.
- `HistoryTimeline Specialist`
  Extracts prior stones, prior procedures, family history, and support context.
- `SymptomsImaging Specialist`
  Extracts symptoms, symptomatic state, stone features, and imaging findings.
- `ProcedureDevice Specialist`
  Extracts surgeries, devices, access methods, and peri-procedural details.
- `Medication Specialist`
  Extracts medication exposure and its attributes.
- `OutcomeComplication Specialist`
  Extracts side effects, outcomes, follow-up, diet, and recommendation facts.
- `Adjudicator`
  Merges duplicates and resolves conflicts.
- `Gap Miner`
  Captures unmapped concepts and recurring misses.
- `Review Router`
  Sends conflicts and gaps to review.

## Invariants

- Accepted events must anchor to text present in the note.
- Cross-sentence evidence must contain more than one anchor.
- Projection writes answers only from stored events or stored timeline facts.
- Failed writes must not mutate accepted event state.
- Irrelevant notes must not activate specialist families by default.
- Unmapped concepts must enter the gap queue.
- Timeline questions must not be answered from one note in isolation.

## What The New Ontology Actually Is

In this design, the ontology is no longer one giant questionnaire.

It becomes a combination of:

- family specs
- event types
- attribute rules
- relation rules
- normalization rules
- projection rules

That structure matches the real problem better because the notes contain facts first, not questionnaire rows.

## Review And Growth

The system still needs a way to grow when notes say things the schema does not yet capture.

That is the job of `GapRecord`.

Every gap record should keep:

- raw concept text
- family
- supporting evidence
- note context
- frequency across notes and patients
- reviewer decision

That gives you a review queue driven by actual corpus evidence instead of intuition alone.

## Acceptance Criteria

The redesign is working when:

- the model extracts a compact event graph instead of a pile of isolated answers
- every accepted event is auditable back to exact note evidence
- one extracted event can support multiple clinician questions
- the tool surface prevents hallucinated spans
- irrelevant notes are filtered before expensive extraction
- ontology growth happens through a reviewable gap queue

That is the path from “LLM helps me label” to “I can answer clinician questions over notes at scale without turning the system into a hodgepodge mess.”

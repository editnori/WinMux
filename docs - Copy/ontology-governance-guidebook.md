# Ontology Governance Guidebook

This guide is the working contract for ontology growth in the event-first annotation system.

It does not replace the event-first architecture. It makes the growth rules explicit so schema, terminology, runtime behavior, tests, and audits stay aligned.

## Source Of Truth

The ontology now has four explicit layers:

1. `annotator/annotation/core/event_families.py`
   Defines the extraction schema: families, event types, attributes, and allowed relations.
2. `annotator/annotation/core/ontology/terminology.py`
   Defines canonical `preferred_term` aliases and controlled attribute vocabularies.
3. `annotator/annotation/core/ontology/policy.py`
   Defines machine-readable attribute policy for every declared `(family, event_type, attribute)` tuple.
4. `annotator/annotation/core/mcp/`
   Enforces the read/write contract through the live MCP runtime. `server.py` is the thin MCP facade; the real read/write contract lives in `read_api.py`, `mutations.py`, and `finalize.py`.

The new policy layer is the bridge between schema and runtime:

- `attribute_kind`: `controlled`, `semi_structured`, or `free_text`
- `canonical_style`: how values should look when canonical
- `allow_provisional`: whether open-text/passthrough values are acceptable
- `controlled_values_exposed`: whether the bundle should expose canonical values to the agent

## Naming Rules

### `preferred_term`

`preferred_term` is the canonical clinical concept name, not the raw chart string.

Rules:

- Put laterality, size, severity, timing, route, and status in attributes when the schema supports them.
- Use one canonical concept per idea.
- Do not invent a new `preferred_term` when the phrase is just a spelling, wording, or modifier variant of an existing concept.

Examples:

- `"left distal ureteral stone"` -> `preferred_term="ureteral stone"` with attributes like `laterality`, `location`, `size`
- `"moderate hydronephrosis"` -> `preferred_term="hydronephrosis"` with `severity="moderate"`
- `"stone related symptoms"` -> `preferred_term="stone-related symptomatic presentation"`

### `concept_id`

`concept_id` is the stable identity handle carried on stored events.

Rules:

- canonical concepts may omit `concept_id` and rely on `preferred_term`
- caller-supplied `concept_id` values are retained for `CUSTOM` concepts
- otherwise the runtime can mint a provisional ID for `PROVISIONAL` concepts
- `build_concept_id()` in `annotator/annotation/core/ontology/terminology.py` is still the deterministic builder when the runtime needs to mint one

Example deterministic ID:

- `symptoms_imaging.imaging_finding.ureteral_stone`

### Raw Note Wording

Raw note wording stays in evidence anchors and `raw_mention`. It is not the canonical name.

### Projection Boundary

Accepted events are not automatically projectable answers.

- `CANONICAL` events can project clinician-facing answers when they match resolver rules.
- `CUSTOM` and `PROVISIONAL` events remain stored extraction outputs, but the projector intentionally excludes them until they canonicalize or the ontology grows to absorb them.

## Attribute Rules

Every declared attribute now has explicit policy in `annotator/annotation/core/ontology/policy.py`.

### `controlled`

Use when the value belongs to a closed vocabulary that should be exposed to the agent.

Examples:

- `laterality`
- `severity`
- `drug_class`
- `recommendation_type`

Requirements:

- must have a canonical map in `ontology/terminology.py`
- should not rely on provisional free-form values
- controlled values are exposed in `get_family_bundle()`

### `semi_structured`

Use when the shape is constrained but the value is not a closed enum.

Examples:

- `size`
- `dose`
- `procedure_date`
- `comparison_reference`
- `timeframe`

Requirements:

- no controlled map required
- runtime preserves the value as submitted
- tests treat it as intentional passthrough, not ontology drift

### `free_text`

Use when the system needs the value but cannot realistically pre-enumerate it.

Examples:

- `social_context_fact.value`
- `imaging_finding.anatomic_detail`
- `recommendation_event.qualifier`

Requirements:

- no controlled map required
- allow provisional values by default
- only use when a controlled enum would be brittle or low-yield

## Decision Table For New Terms

When a new note phrase appears, choose one path:

### Add an alias

Use when the phrase is just another surface form of an existing concept.

Examples:

- spelling variants
- wording variants
- location/laterality modifiers already represented by attributes

Action:

- add to `_EVENT_TERMS` or `_ATTRIBUTE_TERMS`
- do not add a new canonical concept

### Add a new canonical term

Use when the phrase represents a clinically distinct concept that the current canonical set does not cover.

Action:

- add a new canonical value in terminology
- decide whether it belongs under an existing event type or exposes a schema gap

### Add a new attribute value

Use when the event type is correct but an enum-like attribute is missing a canonical value.

Action:

- update `_ATTRIBUTE_TERMS`
- do not create a new event concept

### Record a schema gap

Use when the concept cannot be represented cleanly by the current event type plus attributes.

Action:

- call `submit_gap(...)`
- keep the evidence anchors
- use the gap bucket to route growth work

## Gap Buckets

`submit_gap()` now classifies gaps into reviewable buckets:

- `canonical_alias_gap`
- `new_canonical_term`
- `attribute_value_gap`
- `schema_gap`
- `uncertain_normalization`
- `other`

These are not cosmetic. They determine how backlog items are ranked in the ontology drift report.

## Agent Bundle Contract

`get_family_bundle()` now returns:

- a compact extraction brief by default
- full schema / policy / registry detail only when explicitly requested

The compact extraction brief exposes, per event type:

- preferred terms
- controlled attributes and values
- `must_capture`
- `auto_inferred`
- family-level `coverage_goals`

The full ontology policy summary still exposes, per event type:

- per-attribute policy metadata
- controlled attributes
- passthrough attributes
- free-text attributes
- semi-structured attributes
- canonical controlled values when exposure is allowed

This is the main anti-drift improvement for annotation agents. The system now compiles registry intent and policy into a smaller, extraction-ready contract instead of forcing the model to read raw rows every time.

## Review Policy

Most growth should stay automated.

### Auto-accept

- new alias additions for an existing canonical concept
- new controlled attribute aliases that collapse to an existing canonical value
- new semi-structured passthrough examples that fit an existing policy

### Manual review required

- new canonical concept additions
- schema changes
- collisions where one raw phrase could map to multiple canonicals
- disputed irrelevance buckets
- uncertain normalization gaps

## Relevance Edge Cases

These are explicit review rules, not ad hoc exceptions.

- Negative stone workups belong in the graph when the note clearly documents imaging ordered for flank pain, hematuria, stone evaluation, or rule-out obstruction. Capture the imaging findings as negated rather than dropping the note as irrelevant.
- Incidental device sightings on unrelated imaging do not make a note stone-relevant by themselves. A ureteral stent seen on a cellulitis CT is not a rescue condition.
- Current-admission summaries embedded in PT/OT/therapy or care-management notes count when they explicitly restate the active stone hospitalization, current obstruction, or decompression plan.
- Medication-only fragments such as isolated `Bicitra`, `tamsulosin`, or anticoagulation holds do not rescue a note without explicit stone/urology context in the current note text.
- Nephrostomy / PCN / stent mentions tied to malignancy, extrinsic obstruction, or non-stone post-operative complications stay outside the stone graph unless the note itself also documents stone disease.

## Audit Loop

The v1 audit loop lives in `annotator/annotation/tools/ontology_audit.py`.

It writes:

- `annotator/data/ontology_audit_v1/false_irrelevant_report.json`
- `annotator/data/ontology_audit_v1/ontology_drift_report.json`
- `annotator/data/ontology_audit_v1/run_health_report.json`
- `annotator/data/ontology_audit_v1/retrieval_spike_report.json`

Run it with:

```bash
cd annotator
python3 -m annotation.tools.ontology_audit \
  --output-dir data/ontology_audit_v1
```

### False-Irrelevant Report

Purpose:

- scan the full corpus
- find notes marked `irrelevant` even though lexical stone hits remain
- bucket them by suppression pattern

Use it to drive planner fixes before broad ontology cleanup.

### Ontology Drift Report

Purpose:

- find provisional and custom event hotspots
- cluster gaps by family and bucket
- measure family sparsity
- surface disagreement hotspots across repeated runs

Use it to decide whether a hotspot should become:

- alias backfill
- new canonical term
- new attribute value
- schema change
- permanent/manual-review bucket

### Run Health Report

Purpose:

- separate ontology issues from operational failures
- bucket failures like usage limits, timeouts, missing artifacts, and workflow/runtime errors

Use it before blaming the ontology for poor run output.

## Retrieval Spike Policy

`bb25` is explicitly scoped as a retrieval experiment, not an ontology redesign.

References:

- [bb25](https://github.com/instructkr/bb25)
- [txtai scoring docs](https://neuml.github.io/txtai/embeddings/configuration/scoring/)

Rules:

- evaluate only against the evidence-candidate ranking path
- do not let it change planner logic
- do not let it change schema or canonical naming
- keep it only if the fixed benchmark in `retrieval_spike_report.json` materially beats the current retriever at top-k recall

If the backend is unavailable or does not beat the current retriever, the decision is `drop`, not “maybe later”.

## Required Checks After Ontology Changes

Run at minimum:

```bash
python3 -m pytest -s annotator/annotation/tests/test_event_planner.py -q
python3 -m pytest -s annotator/annotation/tests/test_event_first_runtime.py -q
python3 -m pytest -s annotator/annotation/tests/test_ontology_consistency.py -q
python3 -m pytest -s annotator/annotation/tests/test_ontology_audit.py -q
```

For final verification:

```bash
python3 -m pytest -s annotator/annotation/tests -q
```

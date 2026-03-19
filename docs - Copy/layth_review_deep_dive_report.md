# Layth_review deep-dive system review

Reviewed snapshot: `Layth_review-aed5595.zip` (unpacked and inspected locally)

## Executive summary

This is not a random pile of regexes wearing a lab coat. It is a real event-first clinical annotation system with a stronger architecture than the repo’s surface mess suggests.

The core design is sound:

- read a note once
- build a note-grounded event graph
- project clinician questions from events instead of doing direct question answering
- keep business logic in code rather than asking the model to remember 100+ downstream rules

The strongest parts are the event-first projection architecture, the shared proposal-validation layer, the prepared-packet extraction path, and the ontology governance split across schema, terminology, and attribute policy.

The weakest parts are the same places most LLM systems go a little goblin mode:

- semantic meaning is still too often “corrected” after extraction by deterministic code
- the planner is still mostly lexical routing with carefully grown exception logic
- the registry is workbook-driven but mapped into the runtime through heuristics that are serviceable, not elegant
- the current codebase contains conceptual drift between older question-first ideas, archived audits, legacy labeled data, and the live runtime

My highest-confidence recommendation is this:

**keep the event-first architecture, keep the projection layer, keep validation guardrails, but move more semantic responsibility to the model at extraction time and make deterministic code act as a validator rather than a semantic co-author.**

That one boundary decision explains most of the current complexity.

---

## What I reviewed

I did not treat the repo as folklore. I inspected the live code, docs, tests, and note data directly.

What I verified locally:

- full test suite: **993 passed in 21.77s**
- corpus: **44 patients, 9,869 notes**
- note types: **6,346 OUT / 2,330 INP / 705 RAD / 265 ED / 117 DS / 106 ADM**
- clinician registry entries: **216 total**
- implemented registry rows: **130**
- deferred structured-data rows: **86**
- current runtime ontology: **5 families / 10 event types**

Important correction: the live repo is **not** the 10-family system described in the external summary you pasted. The current runtime consistently uses **5 families**:

1. `history_timeline`
2. `symptoms_imaging`
3. `procedure_devices`
4. `medications`
5. `outcomes_complications`

Those families expand to **10 event types** total:

- `history_fact`
- `social_context_fact`
- `symptom_presentation`
- `imaging_finding`
- `procedure_event`
- `device_event`
- `medication_exposure`
- `adverse_effect`
- `outcome_event`
- `recommendation_event`

That mismatch matters, because it changes how you should think about ontology scope, prompt design, and the role of downstream normalization.

---

## What the current system actually is

At a high level, the repo implements an event-first annotation runtime with two surfaces:

- **interactive MCP path** for note-by-note review and debugging
- **prepared packet path** for batch annotation, which is now the production default

The actual flow looks like this:

1. **Open session**
   - load note text from `consolidated_dataset.json`
   - build `SectionDetector`, `NoteIndex`, `PositionResolver`, `NoteEvidenceRetriever`
   - initialize session state and output paths

2. **Plan note**
   - decide relevance: `stone_relevant`, `possibly_relevant`, or `irrelevant`
   - activate families
   - collect matched signals and route priorities

3. **Prepare note-specific evidence**
   - interactive path: `prepare_active_families()` / `prepare_family(...)`
   - batch path: `build_prepared_packet(...)`

4. **Extract events**
   - model proposes typed events using candidate ids / evidence refs
   - shared validation resolves anchors, binds candidates, canonicalizes concepts, and enforces schema

5. **Enrich and normalize**
   - deterministic enrichment fills attributes
   - graph normalization adjusts assertion / temporality / anchor cleanup / dedupe / relation synthesis

6. **Finalize and project**
   - compute final relevance label after actual event review
   - prune events if note becomes irrelevant after review
   - project clinician-facing answers from events
   - save `.event.json` artifact + metrics

That is the correct mental model for this codebase.

---

## Architecture map: where the real complexity lives

If you feel the repo is a hodgepodge, your intuition is not wrong. But it is not uniformly messy. The complexity is concentrated.

The main complexity centers by file size are:

- `planning/prepared_extraction.py` — **2,788 lines**
- `planning/swarm_orchestrator.py` — **1,400 lines**
- `mcp/read_api.py` — **1,139 lines**
- `ontology/event_terms.py` — **1,089 lines**
- `ontology/attribute_terms.py` — **1,033 lines**
- `enrichment/medications.py` — **746 lines**
- `event_graph_normalization.py` — **694 lines**
- `planning/planner.py` — **641 lines**
- `mcp/mutations.py` — **637 lines**
- `enrichment/dispatcher.py` — **614 lines**
- `proposal_validation.py` — **555 lines**

That distribution tells a clear story.

This system is not bottlenecked by one gigantic prompt. It is bottlenecked by:

- packet construction
- routing
- schema/ontology normalization
- medication enrichment
- post-hoc semantic cleanup

That is where most of your future work should stay focused.

---

## The ontology system: how it really works

The ontology is better than it first looks. It is not just a list of labels.

It has **four distinct layers**.

### 1. Extraction schema layer

File: `annotation/core/event_families.py`

This is the runtime schema layer. It defines:

- the 5 families
- the 10 event types
- family descriptions
- family guidance for extraction
- allowed attributes per event type
- allowed relation types per event type
- keyword hints for routing/retrieval

This is the **structural ontology**: what kinds of things can exist in the event graph.

Example:

- `symptoms_imaging/imaging_finding` supports attributes like `laterality`, `location`, `severity`, `size`, `hounsfield_unit`, `status`, `clinical_significance`
- `procedure_devices/procedure_event` supports `laterality`, `status`, `approach`, `procedure_date`, `target_finding`, operative parameters
- `medications/medication_exposure` supports `drug_class`, `dose`, `route`, `frequency`, `duration`, `date`, `indication`, `context`, `status`

### 2. Terminology layer

Files:

- `ontology/event_terms.py`
- `ontology/attribute_terms.py`
- `ontology/terminology.py`

This is the **canonicalization layer**.

It maps chart language to controlled terms and canonical attribute values.

For events, I verified the current controlled term coverage includes:

- `history_fact`: 15 canonical terms / 102 aliases
- `social_context_fact`: 14 canonical terms / 27 aliases
- `symptom_presentation`: 1 canonical term / 18 aliases
- `imaging_finding`: 23 canonical terms / 192 aliases
- `procedure_event`: 23 canonical terms / 145 aliases
- `device_event`: 20 canonical terms / 85 aliases
- `medication_exposure`: 88 canonical terms / 184 aliases
- `adverse_effect`: 29 canonical terms / 66 aliases
- `outcome_event`: 16 canonical terms / 83 aliases
- `recommendation_event`: 23 canonical terms / 88 aliases

This is a real clinical lexicon, not a toy map.

### 3. Attribute policy layer

File: `ontology/policy.py`

This is the most underrated part of the system.

It explicitly governs each `(family, event_type, attribute)` triple with:

- `attribute_kind`: `controlled`, `semi_structured`, `free_text`
- canonical style rules
- whether provisional values are allowed
- whether controlled values should be exposed to the model

That means the ontology is not only saying **what terms exist**, but also **how strict the runtime should be about each attribute**.

This is very good design.

Examples:

- `laterality`, `severity`, `drug_class`, `recommendation_type` are controlled
- `dose`, `size`, `procedure_date`, `timeframe` are semi-structured
- some details like `social_context_fact.value` or `imaging_finding.anatomic_detail` remain free text

This layer is the bridge between schema purity and real clinical notes, which are gloriously cursed.

### 4. Clinician registry layer

Files:

- workbook: `data/Variable Prioritization Summary v2.0.xlsx`
- loader: `registry/loader.py`
- mapping heuristics: `registry/resolvers.py`

This layer is not the extraction ontology. It is the **question catalog**.

I verified the registry currently contains **216 entries**:

- `history_timeline`: 113
- `procedure_devices`: 66
- `outcomes_complications`: 17
- `symptoms_imaging`: 9
- `medications`: 7
- `unclassified`: 4

By scope:

- `note`: 112
- `patient_timeline`: 94
- `index_relative`: 7
- `encounter`: 3

By implementation status:

- `implemented`: 130
- `deferred`: 86

This is important:

**the registry is clinician intent, not extraction truth.**

That distinction exists in the code, but it is not always explained clearly enough in the docs.

---

## My view of the ontology design

### What is good

The ontology is strongest where it separates concerns cleanly:

- schema decides what can exist
- terminology decides what names are canonical
- policy decides how strict each attribute should be
- registry decides what clinicians want answered
- projector decides which events count toward those answers

That is the right architecture.

### What is weak

There are still three conceptual leaks.

#### 1. Registry and ontology are still psychologically entangled

Even though the code separates them, the mental model in the repo still sometimes treats the registry as if it defines the extraction shape.

It should not.

Clinician questions should drive:

- coverage targets
- evaluation
- projection rules

They should **not** be allowed to distort extraction into a question-shaped ontology.

#### 2. Five families are practical, but internally overloaded

The 5-family runtime is simpler than the old sprawling family picture, which is good. But some families now absorb multiple conceptual domains.

The most overloaded family is `outcomes_complications`, which currently contains:

- complications / adverse effects
- outcome statements
- recommendations / follow-up

That is acceptable operationally, but ontologically these are different beasts.

I would keep the visible 5-family interface for the model if you like the simplicity, but internally I would treat those as clearer subdomains for evaluation and packet building.

#### 3. Registry mapping is heuristic and therefore brittle

`registry/loader.py` and `registry/resolvers.py` infer family, scope, shape, and resolver behavior from clinician wording using string heuristics.

This works.

It is also exactly the sort of thing that quietly rots when people add rows to the spreadsheet with new wording. It is a governance risk rather than an immediate bug.

---

## How the annotator runtime works, in concrete terms

### 1. Note loading and session bootstrap

Files:

- `infra/note_loader.py`
- `mcp/state.py`
- `nlp/section_detector.py`
- `nlp/note_index.py`
- `nlp/position_resolver.py`
- `nlp/evidence_retriever.py`

When you open a note, the runtime builds a note-local workspace with:

- section segmentation
- sentence/span indexing
- quote-to-offset resolution
- lexical retrieval over note text

That is a good pattern. It means evidence is always grounded back to source text rather than living as vibes.

The evidence retriever is lexical, not semantic. That is deliberate. It uses:

- inverted-index lookup over note lines
- phrase bonuses
- short-token whitelisting for clinical abbreviations like `ct`, `jj`, `iv`
- lab-line penalties so medication retrieval is not contaminated by chemistry tables
- bounded quote/context windows

This is one of the healthier pieces of deterministic code in the repo because it is doing retrieval, not pretending to do deep meaning.

### 2. Planning / routing

File: `planning/planner.py`

The planner does three jobs:

- relevance classification
- family activation
- routing metadata for retrieval / packet building

It uses lexical patterns and note-type context to decide whether the note is:

- `stone_relevant`
- `possibly_relevant`
- `irrelevant`

It also tracks:

- direct stone focus
- non-stone primary context
- incidental stone context
- scheduling-sheet context
- structural-only overlap signals

This is where recent cancer/urology false-positive handling lives.

Example from the live repo:

For note `OUT.IDX.0002772`, the planner still sees overlapping structural signals like:

- hydronephrosis
- nephrostomy
- flank pain
- hematuria

But because the note is centered on bladder/prostate cancer follow-up, the system classifies it as **irrelevant** and activates **no families**.

That is the correct behavior for this project’s target graph.

### 3. Prepared packet path

File: `planning/prepared_extraction.py`

This is the most important architectural upgrade in the current repo.

Instead of asking the model to drive the whole session interactively, the batch runtime now builds a compact note-local extraction packet that contains:

- routing summary
- selected sections
- active families
- candidate evidence handles
- family-specific extraction brief
- packet recipe and policy flags
- expected event count range
- candidate-level restrictions like `allowed_event_types`, `resolved_preferred_term`, and `requires_subspan_text`

That is excellent design.

It turns the model from a free-roaming tourist into a constrained specialist with a map and a flashlight.

The packet recipes are especially useful. The system distinguishes note classes like:

- `simple_stone`
- `outpatient_obstruction`
- `dense_ed`
- `radiology`
- `weak_signal_review`

That is exactly the right direction for token-efficient, high-precision extraction.

### 4. Model extraction contract

Files:

- `mcp/extraction_brief.py`
- `planning/swarm_prompt.py`

The prompt contract is more mature than I expected.

The family brief explicitly distinguishes:

- `must_capture`
- `auto_inferred`
- preferred term lists
- coverage goals

That means the model is told not to waste effort restating details the deterministic code can fill.

This is smart in principle.

But it also creates a hidden risk:

The more you declare fields “auto-inferred,” the more you pressure downstream code to silently decide meaning after the model has already done interpretation.

That is where several bug classes come from.

### 5. Shared proposal validation

File: `proposal_validation.py`

This is one of the strongest modules in the repo.

It is the right kind of deterministic layer.

It does things code should do well:

- verify family / event type are legal
- enforce allowed attribute keys
- resolve evidence refs and quotes to anchors
- canonicalize event identity
- apply candidate-local restrictions
- enforce subspan narrowing when one candidate contains multiple facts
- normalize assertion / temporal enums into allowed forms

This shared module prevents the interactive and prepared paths from drifting apart.

That is absolutely the correct move.

### 6. Enrichment

Files:

- `enrichment/dispatcher.py`
- `enrichment/medications.py`
- `enrichment/medication_guardrails.py`
- plus imaging / procedure helpers

This is where the system gets both powerful and dangerous.

The good version:

- fill obvious laterality, location, route, units, frequencies, statuses
- check medication plausibility against a drug guardrail table
- rescue missing attributes when evidence is local and unambiguous

The bad version:

- reinterpret event meaning after extraction
- bind the wrong numeric or status cue from neighboring text
- apply broad textual cues to the wrong target

The medication path is the clearest example of both the good and the bad.

The repo now includes a serious medication plausibility resource:

- `drug_guardrails.json` — **10,086 lines**
- combo-pill examples and medication-specific guardrails

That is good guardrail logic.

The risky part is the regex-driven attribute search windows in `medications.py`, which are necessarily heuristic and can still overreach or underreach.

### 7. Graph normalization and relation synthesis

Files:

- `event_graph_normalization.py`
- `event_relations.py`

Normalization currently does a lot:

- anchor cleanup
- canonical raw mention handling
- temporal inference
- assertion inference
- event-specific attribute correction
- dedupe / merge
- deterministic relation synthesis

The relation synthesis layer is narrow and understandable. It mostly links things like:

- procedures/devices to imaging findings through target hints
- outcomes to most recent procedures
- adverse effects to suspected procedure/device/medication causes
- recommendations to imaging findings in the same note

That is fine as a starter relation layer.

The bigger issue is not relations. It is that normalization still owns too much semantic judgment.

### 8. Projection

File: `event_projector.py`

This is one of the best design choices in the system.

The projector converts events into clinician-facing answers using deterministic resolver rules.

It also correctly excludes events that should not answer “did this happen?” style questions, including:

- negated events
- future events
- provisional/custom concepts
- planned/scheduled/deferred/canceled procedures or devices
- historical events outside timeline scope

That is exactly where business logic belongs: after extraction, in code, not hidden in prompts.

### 9. Finalization

File: `mcp/finalize.py`

Finalization is not a dumb save step. It recalculates note relevance after actual extraction.

That means the planner can say “possibly relevant,” but if review yields no supported stone evidence, the note can become `irrelevant_after_review`.

That is a very good idea.

It makes note relevance evidence-driven instead of planner-driven.

---

## Concrete examples from the live system

### Example A — outpatient obstruction note routes cleanly

I opened real note `HID.2572100453940710000 / OUT.IDX.0001170`.

The planner returned:

- relevance: `stone_relevant`
- active families: `symptoms_imaging`, `procedure_devices`
- stone signals: `hydronephrosis`, `upj obstruction`, `upj`, `flank pain`, `hematuria`

The prepared packet classified it as recipe `outpatient_obstruction` and selected sections:

- `ASSESSMENT`
- `IMAGING`
- `CC`

It produced candidate-local restrictions such as:

- the CC candidate resolved to `imaging_finding / obstructive uropathy`
- the CT sentence resolved to `imaging_finding / hydronephrosis`
- the plan sentence containing `possible robotic pyeloplasty pending results of renogram` routed to `procedure_event / pyeloplasty`

This is the system at its best: candidate-local constraints are doing useful narrowing before the model ever answers.

### Example B — manual end-to-end run behaves sensibly

I manually proposed two events on that note:

1. `imaging_finding / hydronephrosis`
2. `procedure_event / pyeloplasty`

After finalization, the saved artifact contained:

- hydronephrosis with attributes inferred from evidence:
  - `laterality = LEFT`
  - `location = UPJ`
  - `attribution = CT`
- pyeloplasty was automatically normalized to:
  - `assertion_strength = POSSIBLE`
  - `temporal_status = FUTURE`
  - `status = planned`

That matters because the projector then answered only the hydronephrosis questions and **did not** falsely count pyeloplasty as completed.

That is the correct interaction between normalization and projection.

### Example C — cancer-context suppression works in the live repo

I opened note `HID.6578942335485340000 / OUT.IDX.0002772`.

The note contains overlapping urologic terms and symptoms, but it is fundamentally a bladder/prostate cancer follow-up note.

The current planner marked it:

- `relevance_label = irrelevant`
- `active_families = []`

The prepared packet therefore returned no active families.

That is exactly the false-positive class you were discussing, and the current code now suppresses it correctly.

### Example D — medication enrichment is clever, but still the most delicate area

I directly exercised the medication enrichment logic on representative strings.

The current code correctly handles:

- `IV vancomycin 750 mg every 12 hours`
  - `dose = 750 mg`
  - `route = IV`
  - `frequency = Q12H`
- `daily tamsulosin 0.4 mg`
  - `dose = 0.4 mg`
  - `frequency = daily`
- `Continue ASA 81 mg daily. Start TMP-SMX for UTI ...`
  - TMP-SMX does **not** inherit aspirin dose or frequency
- `... he has taken two doses bactrim. He has been holding his ASA 81mg ...`
  - TMP-SMX does **not** inherit aspirin hold status or aspirin dose

So yes: the medication layer is materially better than a naive regex swamp.

But it is also still exactly where your future regressions are most likely to hide.

---

## What the system gets right

### 1. Event-first is the correct backbone

This is the right design for your problem.

Directly asking a model 100+ clinician questions per note would be noisy, redundant, hard to cite, and difficult to stabilize.

Event-first gives you:

- one note interpretation
- reusable downstream answers
- auditable evidence anchors
- easier consistency evaluation
- easier ontology growth

Do not throw that away.

### 2. Projection belongs in code

This is one of the most correct boundaries in the repo.

Whether a future/planned procedure should answer “was this done?” is not an extraction problem. It is a business rule.

The repo gets that mostly right.

### 3. Prepared packets are the right production surface

The prepared packet path is materially better than letting the model repeatedly ping the MCP tools in a wide-open loop.

Why it is good:

- lower token waste
- more stable context
- family-local candidate constraints
- better opportunity for evals and batch reproducibility

This should remain your production backbone.

### 4. Shared validation is exactly the right kind of determinism

Validation should be centralized, reproducible, and boring.

The repo now does this well.

### 5. The ontology has a real governance layer

A lot of clinical extraction repos talk about ontology but really mean “big synonym list.”

This repo actually has:

- controlled event terms
- controlled attribute vocabularies
- per-attribute policy
- provisional/custom concept handling
- gap classification buckets

That is a serious foundation.

### 6. Test coverage is strong

The full test suite passes, and the tests are not trivial. They cover:

- planner behavior
- prepared packet generation
- validation
- normalization
- medication enrichment edge cases
- projector behavior
- runtime boundaries

That gives you permission to refactor aggressively, because you are not operating without a parachute.

---

## What is brittle or conceptually off

### 1. The system still asks code to do meaning work

This is the central issue.

The model extracts an event. Then code often tries to:

- reinterpret whether it is possible vs confirmed
- reinterpret planned vs completed
- reinterpret device state
- attach missing medication attributes from neighboring text
- fix laterality / location / relation targets from contextual cues

Some of this is useful.

Too much of it becomes semantic post-processing, which is where deterministic code starts doing low-budget NLU with sharp little regex teeth.

That is where many historical regressions came from, and it will remain the main failure class unless you change the ownership boundary.

### 2. Planner precision is still fundamentally lexical

The planner is much better now, especially for cancer-context suppression, but it is still basically a pattern router with exception logic.

That means its failure modes are inherently about lexical overlap:

- non-stone urologic structure notes
- historical-only notes
- templated carry-forward text
- negative workups that should remain relevant
- weak-signal notes that need actual interpretation, not just keyword counts

I would not throw away the planner. But I would stop expecting lexical planning alone to be your final relevance brain.

### 3. `prepared_extraction.py` is carrying too much orchestration intelligence

At 2,788 lines, `prepared_extraction.py` is doing a lot:

- note plan interaction
- packet recipe choice
- family selection behavior
- section packing
- candidate filtering
- policy flags
- artifact prior integration
- response parsing / repair loops

That is where the repo feels most like an evolving system rather than a settled architecture.

It is not bad, but it is where I would expect complexity to keep accreting unless you formalize the packet spec more aggressively.

### 4. Legacy data creates conceptual drag

The source dataset still contains legacy adjudicated `events` inside `consolidated_dataset.json` for many notes.

I counted **1,597 notes** with legacy embedded events, totaling **5,237** event records.

Those legacy events are not the live runtime ontology. They reflect an older labeling worldview.

This is not a code bug, but it is a conceptual trap when people compare old labels to new extraction without explicitly versioning the ontology boundary.

### 5. Relations are still narrow and underpowered

The relation synthesis is understandable but not rich.

That is fine for now.

But if you eventually want more clinically expressive stories — for example linking symptom -> imaging finding -> decompressive device -> adverse effect -> follow-up plan — you will need a more deliberate relation strategy than the current deterministic edge filler.

### 6. Docs and mental models are drifting

The repo docs are mostly coherent around the current 5-family runtime, but external descriptions and older internal narratives still float around with different system shapes.

That is dangerous because architectural confusion becomes annotation inconsistency long before it becomes a failing test.

---

## What I would change architecturally

### 1. Make the semantic ownership boundary explicit

This is the main redesign.

#### The model should own

- assertion meaning in context (`CONFIRMED`, `NEGATED`, `POSSIBLE`, `REPORTED`)
- temporal interpretation (`CURRENT`, `HISTORICAL`, `FUTURE`)
- whether a procedure/device mention is completed vs planned vs discussed
- medication attribute binding when multiple drugs share one line
- whether two mentions are one evolving fact or two separate events

#### Code should own

- schema validity
- ontology canonicalization
- evidence resolution to exact anchors
- candidate-local restrictions
- plausibility guardrails
- projection/business rules
- dedupe and stable artifact assembly

That is the clean boundary.

Right now the system often lets the model speak, then lets the code interrupt to “clarify.” That is a recipe for semantic drift.

### 2. Keep enrichment, but demote it from semantic editor to attribute extractor

I would keep deterministic enrichment for things like:

- units
- simple numeric extraction
- exact laterality when it is explicitly in the same anchor
- canonical drug class lookup
- route normalization (`intravenous` -> `IV`)

I would **not** let enrichment override explicit model semantics unless:

- the model output is invalid under schema
- the evidence directly contradicts the field
- a hard guardrail fires

In other words:

**guardrails may veto or null a value; they should rarely invent meaning.**

### 3. Make candidates more mention-centric

The packet system is already heading this way, but I would go further.

Right now some candidates still represent broad sentence or line chunks that may contain multiple facts.

I would push toward candidates that carry more explicit local structure, for example:

- canonical suggestion
- allowed event types
- one or more extracted local spans
- optional attribute hints derived from exact subspans
- explicit ambiguity flags

That reduces the need for downstream subspan rescue logic and makes extraction more traceable.

### 4. Formalize packet recipes as versioned schemas, not just evolving Python logic

Your packet builder is already smart.

The next step is to make it more declarative.

I would move more packet behavior into a versioned recipe spec that declares:

- section priority
- candidate caps
- candidate inclusion/exclusion rules
- expected event count range
- priority concepts
- negative constraints

Because right now a lot of your behavior is encoded as Python conditionals. That makes it harder to audit recipe drift.

### 5. Add a lightweight second-stage relevance judge

Do not replace the lexical planner. Wrap it.

My preferred design:

- stage A: fast lexical planner for candidate family routing
- stage B: lightweight relevance judge over the prepared packet, not the whole raw note

That judge should answer a narrow question:

> does this packet contain supported, note-current kidney-stone evidence worth preserving in the event graph?

That is cheaper and more stable than asking a model to read the entire note just to decide relevance.

It would probably kill several remaining false-positive/false-negative classes.

### 6. Separate event ontology from question ontology in docs and code comments

This is already mostly true in code, but I would make it impossible to misunderstand.

I would explicitly document three layers:

1. **event ontology** — what can be extracted
2. **registry/question ontology** — what clinicians ask
3. **projection ontology** — which extracted states answer which questions

That naming alone will reduce a lot of design confusion.

### 7. Treat relations as a second-phase capability, not something to half-infer everywhere

I would keep the current narrow relation synthesis for now, but I would not keep expanding it ad hoc.

Instead:

- first stabilize event fidelity
- then define 3–5 high-value relation types you truly need
- build explicit evals for them
- only then widen relation extraction

Otherwise relation logic becomes another semantic swamp with a flashlight duct-taped to a rake.

### 8. Tighten registry governance

I would stop relying solely on wording heuristics when new registry rows are added.

Each registry row should eventually be able to carry explicit metadata for:

- family
- answer scope
- answer shape
- resolver id
- resolver config
- implemented status

Even if you still auto-infer defaults, the workbook or a checked-in sidecar should be allowed to override them explicitly.

That will reduce silent drift as the registry evolves.

---

## How I would design the system if I were rebuilding it now

I would keep the overall event-first design, but I would reorganize it into five clear layers.

### Layer 1 — note analysis surface

Input:

- raw note text
- note metadata

Output:

- sections
- sentence index
- candidate mentions / evidence windows
- routing signals

This layer stays mostly deterministic.

### Layer 2 — packetized extraction surface

Input:

- note analysis output
- event ontology
- packet recipe

Output:

- compact packet for one note
- note-local candidate ids
- family-local extraction brief

This becomes the main model interface.

### Layer 3 — schema-constrained event extraction

Input:

- prepared packet

Output:

- JSON-schema-constrained event proposals with:
  - family
  - event_type
  - preferred_term
  - evidence refs
  - explicit semantic fields
  - only a minimal set of model-supplied attributes

This is where I would trust the model more.

### Layer 4 — validation and guardrails

Input:

- proposed events
- evidence refs
- ontology
- drug / attribute guardrails

Output:

- accepted events
- rejected items with repair reasons
- gap records

This layer should validate and veto. It should not play doctor-poet.

### Layer 5 — projection and eval

Input:

- normalized event graph

Output:

- clinician-facing answers
- benchmark metrics
- drift reports
- error slices by failure class

This stays deterministic.

That is the system I would want to own long term.

---

## Regex: what I would keep, what I would delete, what I would quarantine

### Keep

Regex is excellent for:

- section header detection
- simple lexical retrieval
- exact unit/number patterns
- local route/frequency normalization
- clause boundary detection as a support feature
- explicit negation cue detection as evidence for the model or validator
- candidate generation / filtering

### Quarantine

Regex is tolerable but dangerous for:

- medication attribute binding across multi-drug spans
- status inference from surrounding clauses
- laterality inference when the span is broad
- recommendation / follow-up interpretation

Use it only when tied to exact local evidence and never as a broad note-level semantic patch.

### Delete or demote

Regex should not be the primary owner of:

- event assertion meaning
- procedure completion vs discussion vs future planning
- device state reasoning across sentences
- subtle negation scope resolution
- relation causality

That is the line.

---

## What I would do next if this were my repo

### First two weeks

1. **Freeze the architecture story**
   - update docs to reflect the live 5-family runtime
   - explicitly distinguish event ontology vs registry vs projection

2. **Create a failure taxonomy**
   - planner false positive
   - planner false negative
   - wrong event type
   - wrong preferred term
   - wrong assertion/temporal status
   - wrong medication binding
   - wrong projection from correct events

3. **Add field provenance**
   For every attribute, track whether it came from:
   - model
   - deterministic enrichment
   - post-hoc correction
   - guardrail nulling

   That one change would make debugging dramatically easier.

4. **Block semantic overwrite by default**
   Make normalization/enrichment opt in to overwrite explicit model semantic fields rather than doing it casually.

### Next month

5. **Add packet-level relevance judge evals**
   Use your review manifests for:
   - cancer-context overlap notes
   - historical-only notes
   - scheduling sheets
   - negative stone workups
   - weak-signal outpatient notes

6. **Refactor medication extraction around mention-local subspans**
   Keep the guardrails, shrink the semantic ambition.

7. **Convert registry inference from pure heuristic mapping to explicit metadata + fallback heuristics**

8. **Split `outcomes_complications` internally for evaluation and recipe logic**
   You can keep one public family while giving yourself cleaner internal reasoning.

### After that

9. **Introduce a lightweight model judge for relevance / rescue on prepared packets**

10. **Decide whether relation extraction is worth the complexity**
    If yes, do it intentionally with dedicated evals.

---

## Bottom line

This system is already closer to the correct architecture than it may feel from inside the code swamp.

The right parts are already present:

- event-first extraction
- grounded evidence
- shared validation
- real ontology governance
- deterministic projection
- strong tests
- prepared packets as the production path

The repo does **not** need a philosophical rewrite.

It needs a cleaner boundary of responsibility.

The simplest statement of my review is:

**keep the architecture, reduce semantic post-processing, trust schema-constrained extraction more, and reserve deterministic code for validation, canonicalization, and projection.**

That change would remove a lot of the complexity you are currently paying for one regression at a time.

And yes, the system has hodgepodge zones. But they are localized. The architecture underneath is real.

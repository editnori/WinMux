# GPT-5.4 Deep Audit

Historical note: this document predates the `annotation/core/` refactor and intentionally references older module paths. Treat it as archival review history, not current runtime guidance.

## Scope

Reviewed:

- `annotator/annotation/event_mcp_server.py`
- `annotator/annotation/event_planner.py`
- `annotator/annotation/event_projector.py`
- `annotator/annotation/event_families.py`
- `annotator/annotation/clinician_registry.py`
- `annotator/annotation/terminology.py`
- `annotator/annotation/event_swarm_prompt.py`
- `annotator/annotation/event_store.py`
- `annotator/annotation/event_session.py`
- `annotator/annotation/evidence_retriever.py`
- `annotator/annotation/section_detector.py`
- `annotator/annotation/tests/*`

Audited data:

- `annotator/data/event_pilot_runs/`: 1,191 artifact files covering 887 unique notes
- `annotator/data/consolidated_dataset.json`: 50-note stratified planner sample

Verification:

- `pytest -q annotator/annotation/tests` -> `265 passed`

## Executive Summary

The current codebase is materially better than the headline pilot metrics suggest. The raw pilot average is low mostly because:

- most pilot notes are irrelevant
- `event_pilot_runs/` mixes multiple run generations
- earlier artifacts predate several planner/runtime fixes already in the repo

The 3 issues that were still worth fixing before a larger run were:

1. planner false positives from generic social/support context plus unrelated meds/outcomes
2. lossy finalization that collapsed accepted multi-anchor evidence and over-merged distinct events
3. missing deterministic `adverse_effect -> medication_exposure` edges

Those were fixed during this audit and are now covered by regression tests.

## High-Priority Fixes Implemented

### 1. Planner precision

File: `annotator/annotation/event_planner.py`

Problem:

- Generic `history_timeline` social/support hits such as `caregiver` and `lives alone` could combine with unrelated medication/outcome keywords and promote clearly non-stone notes to `possibly_relevant`.
- This did not show up strongly in the curated review manifests after the earlier `social history` fix, but it still reproduced on the broader consolidated dataset.

Fix:

- Added `_HISTORY_SOCIAL_ONLY_KEYWORDS`
- Relevance promotion from `history_timeline` now uses only non-social history hits
- `history_timeline` activation for social-only hits is now gated behind an already stone-relevant note

Effect:

- In a 50-note stratified consolidated-dataset sample, `empty-stone-signal but non-irrelevant` notes dropped from `4` to `0`

### 2. Finalization integrity

File: `annotator/annotation/event_mcp_server.py`

Problems:

- `_event_specific_anchor_selection()` collapsed accepted multi-anchor imaging/history evidence to one anchor
- `_find_merge_candidate()` merged events on coarse keys alone
- `_merge_events()` discarded right-side relations
- `_best_matching_imaging_event()` still used lexical event-id ordering

Fixes:

- Preserved all accepted anchors during normalization
- Required shared evidence overlap before merge
- Merged relation sets instead of keeping only the left event’s relations
- Switched imaging target selection to numeric event-id ordering

Effect:

- Cross-sentence evidence now survives finalization intact
- Distinct accepted imaging events with disjoint evidence no longer collapse into one artifact row

### 3. Medication-attributed adverse-effect relations

File: `annotator/annotation/event_mcp_server.py`

Problem:

- Historical artifacts showed medication and adverse-effect events without a `caused_by` edge even when `suspected_cause` was an exact drug mention such as `fentanyl`

Fix:

- Added deterministic medication lookup for `adverse_effect.suspected_cause`

Effect:

- Exact drug-attribution cases now emit `caused_by -> medication_exposure`

## File-by-File Audit

### `event_mcp_server.py`

Current state:

- `plan_note()` is now required before propose/gap/finalize
- lock heartbeat checks are present on propose/revise/gap/finalize
- finalization stages writes in locals before mutating session state
- corrupt artifact loads are handled by `EventArtifactStore`

Issues fixed in this audit:

- multi-anchor evidence collapse
- over-aggressive merge of distinct imaging/history events
- relation loss during merge
- lexical imaging-event ordering
- missing medication cause synthesis for adverse effects

Remaining observations:

- Merge is now intentionally conservative. This is the right tradeoff for graph integrity, but it may allow more duplicate events when the model repeats the same fact in distant sections without overlapping anchors.
- Relation synthesis is still narrow beyond procedure/device targeting and the new medication-cause edge.

Priority: fixed high-risk integrity issues; remaining work is coverage expansion, not correctness repair.

### `event_planner.py`

Current state:

- Earlier fixes for `social history`, obstetric hydronephrosis suppression, PMH suppression, and short-token handling are in place

Issue fixed in this audit:

- social/support context plus generic medication/outcome hits could still create `possibly_relevant` false positives in the full consolidated corpus

Remaining observations:

- Planner is still fully lexical. It will continue to trade some recall for determinism.
- Generic outcome keywords such as `acute kidney injury` and generic medication keywords still create family-signal noise, but they no longer promote irrelevance on their own.

Priority: good enough for large-scale testing after this patch.

### `event_projector.py`

Current state:

- `NEGATED` events are filtered
- `HISTORICAL` events are filtered
- `supporting_event_ids` now only include contributing events for attribute-value projections

Findings:

- No current critical bugs reproduced
- `_run_resolver()` still silently drops unknown resolver ids by returning `None`; this is low risk because implemented resolver configs currently validate cleanly

Priority: no blocker.

### `event_session.py`

Findings:

- `pending_families()` logic is correct
- event/gap counter hydration is correct
- request cache behavior is straightforward and low risk

Priority: no blocker.

### `event_store.py`

Findings:

- Atomic save via temp-file replace is correct
- corrupt JSON/non-dict artifacts are quarantined instead of poisoning future sessions

Priority: no blocker.

### `event_families.py`

Findings:

- Family schema and relation-type constraints are coherent
- Current family keywords are broad by design; planner must continue to be the relevance gate
- `cystoscopy` is already present in `procedure_devices` keywords, so older gap-heavy artifacts on this concept appear to be historical-run behavior, not a current bundle omission

Priority: no blocker.

### `evidence_retriever.py`

Findings:

- Short clinical abbreviation whitelist fix is present (`ct`, `us`, `ed`, `jj`, etc.)
- Phrase bonus improves multi-word retrieval materially
- Retrieval is still line-based and lexical; this is deterministic and cheap, but not semantically deep

Performance note:

- Phrase scoring loops over every line for each multi-word phrase. That is acceptable at current note sizes and candidate counts.

Priority: no blocker.

### `terminology.py`

Findings:

- Current canonical term coverage is materially better than early pilots
- `cystoscopy` is already canonicalized
- Only `3` noncanonical events remained across the deduped artifact set, all medication terms: `warfarin`, `lovenox`, `dicyclomine`

Observations:

- Historical artifacts still show mixed attribute values, but those appear to come from older runs rather than current canonicalization code

Priority: no blocker.

### `clinician_registry.py`

Findings:

- Implemented resolver/schema mismatch audit came back clean: `0` current mismatches
- Hematoma resolver points to `outcomes_complications/adverse_effect`

Coverage reality:

- Total rows: `213`
- Implemented: `18`
- Planned: `35`
- Deferred: `160`

Implemented distribution:

- `medications`: `7`
- `symptoms_imaging`: `5`
- `outcomes_complications`: `2`
- `history_timeline`: `2`
- `procedure_devices`: `2`

Important observation:

- The only implemented `history_timeline` note-scope rows are profession/education. Those generic social questions are what allowed planner overreach until the new social-hit gate was added.

Priority: no blocker for extraction quality, but answer coverage is still intentionally narrow.

### `section_detector.py`

Findings:

- Header coverage is decent for the current clinical note mix
- Gap inference is intentionally conservative
- No current correctness bug reproduced

Priority: no blocker.

### `event_swarm_prompt.py`

Findings:

- Prompt is much stronger on event extraction than relation creation
- It mentions relations in examples, but not as a first-class required pass

Impact:

- This contributes to sparse relation graphs, especially for medication/adverse-effect and outcome/recommendation links

Priority: medium; not a correctness blocker, but worth tightening for higher relation yield.

## Artifact Quality Audit

### Aggregate artifact view

Using the latest available artifact per unique note:

- unique notes: `887`
- relevance mix: `757 irrelevant`, `13 irrelevant_after_review`, `101 stone_relevant`, `16 possibly_relevant`
- average events/note: `0.414`
- average relations/note: `0.071`

Within non-irrelevant notes only:

- non-irrelevant notes: `117`
- average events/note: `3.137`
- average relations/note: `0.538`

Interpretation:

- The raw average is not evidence of universal extraction collapse
- Most pilot notes are irrelevant, and the folder mixes multiple historical run versions

### 30-artifact manual sample

The stratified 30-artifact read showed:

- zero-event `stone_relevant` artifacts were usually planner precision failures, not extractor misses
- the common failure modes were historical-only mentions, incidental stones, and non-stone urology notes
- artifact persistence itself was not the problem; stored events consistently retained evidence anchors

Representative patterns:

- bariatric or counseling notes mislabeled `stone_relevant` due history/risk text
- bladder-mass / non-stone hematuria notes pulled in by weak symptom heuristics
- incidental renal stones correctly represented as a single imaging event, but sometimes still labeled too strongly at note level

### What the artifacts imply

The existing pilot folder is still useful, but it should be read as:

- a mix of old planner behavior
- a mix of older runtime generations
- a decent source for relation-gap diagnosis

It is not a clean measurement of the current code after the recent fixes.

## Planner Accuracy Audit

Method:

- 50-note stratified sample from `consolidated_dataset.json`
- quotas: `ADM 8`, `DS 7`, `ED 10`, `INP 10`, `OUT 10`, `RAD 5`
- same random seed before and after the planner patch

### Before the patch

- `42 irrelevant`
- `4 possibly_relevant`
- `4 stone_relevant`
- `4` non-irrelevant notes had empty `stone_signals`

False-positive pattern:

- `caregiver` / `lives alone` history hits
- plus generic meds or outcome terms
- no actual stone signal

### After the patch

- `46 irrelevant`
- `0 possibly_relevant`
- `4 stone_relevant`
- `0` non-irrelevant notes had empty `stone_signals`

Manual checks:

- `ADM.IDX.0000049` and `OUT.IDX.0005013` remained `irrelevant` correctly; their apparent stone mentions were PMH/problem-list only
- `ADM.IDX.0000075` and `OUT.IDX.0004911` are now correctly `irrelevant` instead of history-only false positives

Conclusion:

- Planner precision is now materially better on the broader corpus, not just the curated review manifests

## Registry Coverage Audit

Current registry implementation is internally consistent but intentionally narrow.

Good:

- resolver configs match current family schemas
- note-scope implemented rows project correctly with current event families

Constraint:

- only `18 / 213` registry rows are implemented

Implication:

- large-scale extraction can now be trusted more than before, but clinician-answer coverage remains limited by registry implementation scope, not by extraction runtime bugs

## Enrichment Quality Audit

Good:

- preferred-term canonicalization is stable
- numeric attributes are preserved
- short-token retrieval fix is present
- current code uses local context windows for imaging attribution instead of whole-note bleed

What still matters:

- attribute enrichment is intentionally heuristic and opportunistic
- broad free-text attributes can still vary semantically unless explicitly canonicalized
- historical pilot artifacts contain mixed value styles that do not fully reflect the current code

Net:

- enrichment is no longer the main blocker; planner precision and relation coverage matter more

## Relations Gap Audit

### Current historical relation inventory

Across deduped pilot artifacts:

- events with at least one relation: `59`
- relation types:
  - `performed_for`: `39`
  - `decompresses`: `22`
  - `temporally_linked`: `2`

By source event type:

- `procedure_event`: `38`
- `device_event`: `25`

What is missing historically:

- `adverse_effect -> medication_exposure`
- most `outcome_event -> procedure_event`
- recommendation follow-up chains
- medication indication/outcome chains

### Why relations are sparse

1. Most notes are irrelevant, so overall relation density is naturally low.
2. Deterministic synthesis previously focused almost entirely on procedure/device targeting of imaging findings.
3. The prompt emphasizes events much more strongly than relations.
4. Older pilot artifacts predate some runtime improvements.

### What changed in this audit

- exact medication-cause adverse-effect edges now synthesize deterministically

### Remaining relation work

Recommended next expansions:

1. `recommendation_event.follow_up_for` using target + active finding context
2. stronger `outcome_event.resolved_after` coverage for device-removal / post-procedure symptom resolution
3. optional prompt pass that explicitly asks the annotator to review likely edges after all events are accepted

## Remaining Non-Blocking Risks Before Large-Scale Testing

1. Relation coverage is still intentionally narrow even after the medication-cause fix.
2. Registry coverage is still only `18` implemented note-scope questions.
3. Planner remains lexical and will continue to miss some semantically stone-relevant notes that avoid explicit stone/urology language.

These are important, but they are no longer integrity blockers for a larger extraction run.

## Bottom Line

The main blockers before a broader run were:

- planner false positives from generic social/support context
- lossy finalization of accepted evidence/events
- missing deterministic medication-cause relations

Those are now fixed and regression-tested.

If the goal is large-scale testing of the extraction pipeline itself, the system is in a reasonable state to run. The next wave of improvement should target:

1. relation coverage
2. registry implementation breadth
3. prompt-level pressure for fuller edge creation on already-correct event graphs

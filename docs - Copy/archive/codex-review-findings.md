# Codex Review Findings

Historical note: this document predates the `annotation/core/` refactor and intentionally references older module paths. Treat it as archival review history, not current runtime guidance.

Reviewed scope:
- `annotator/annotation/event_mcp_server.py`
- `annotator/annotation/event_session.py`
- `annotator/annotation/event_store.py`
- `annotator/annotation/event_planner.py`
- `annotator/annotation/evidence_retriever.py`
- `annotator/annotation/event_projector.py`
- `annotator/annotation/terminology.py`
- `annotator/annotation/clinician_registry.py`

Method:
- Read every function in the target files.
- Cross-checked behavior against `docs/scalable-labeling-design.md`.
- Ran targeted tests:
- `pytest -q annotator/annotation/tests/test_evidence_retriever.py annotator/annotation/tests/test_terminology.py annotator/annotation/tests/test_event_first_registry.py`
- Result: `98 passed`
- `pytest -q annotator/annotation/tests/test_event_first_runtime.py`
- Result: `125 passed`

Important context:
- The current suite has strong happy-path and heuristic-regression coverage.
- Most of the issues below are invariant violations, error-path gaps, or cross-file contract mismatches that the existing tests do not exercise.

## `event_mcp_server.py`

- `High`: the server lets callers bypass the required plan/review flow. `propose_event()` and `submit_gap()` only enforce active-family checks when `session.note_plan` is already populated, and `finalize_note()` does not require `plan_note()` at all. A caller can `open_note()`, skip planning, write any family, and finalize with no family reviews. Refs: `annotator/annotation/event_mcp_server.py:450-453`, `annotator/annotation/event_mcp_server.py:677-680`, `annotator/annotation/event_mcp_server.py:742-771`.
- `High`: finalizing without a plan silently produces empty projections. `finalize_note()` builds `recommended_ids` from `note_plan`; without `plan_note()`, that becomes an empty set, and `project_note_answers(..., allowed_registry_ids=set())` projects nothing. This is a fail-open flow violation followed by a fail-silent projection bug. Refs: `annotator/annotation/event_mcp_server.py:757-771`, `annotator/annotation/event_projector.py:12-36`.
- `High`: unresolved evidence quotes are silently dropped instead of rejecting the request. `_resolve_anchors()` skips unresolved quotes, while callers only fail when zero anchors remain. A multi-quote event can therefore be accepted with a weaker evidence set than the caller submitted. Refs: `annotator/annotation/event_mcp_server.py:468-475`, `annotator/annotation/event_mcp_server.py:582-590`, `annotator/annotation/event_mcp_server.py:682-689`, `annotator/annotation/event_mcp_server.py:819-835`.
- `High`: `open_note()` acquires the note lock before artifact load/hydration, but artifact load errors are not protected by `try/finally`. A corrupt artifact can therefore strand the lock until TTL expiry. Refs: `annotator/annotation/event_mcp_server.py:200-229`, `annotator/annotation/event_mcp_server.py:234-237`, `annotator/annotation/event_store.py:21-25`.
- `High`: `finalize_note()` does not heartbeat or revalidate lock ownership before persisting and releasing. Mutation tools heartbeat the lock; finalization does not. If the lock expired or was stolen, this path can still save an artifact and then ignore a failed release. Refs: `annotator/annotation/event_mcp_server.py:742-793`.
- `High`: `finalize_note()` mutates accepted session state before persistence succeeds. It overwrites `session.events` and `session.projections` before `store.save()`. If save fails, the in-memory session is left partially finalized even though no artifact was written. That violates the stated invariant that failed writes must not mutate accepted state. Refs: `annotator/annotation/event_mcp_server.py:757-789`.
- `High`: `_normalize_assertion_strength()` over-negates affirmative events. Any anchor containing a negation token sets `NEGATED`, so text like `Right ureteroscopy performed without complication` becomes a negated event. This was reproducible directly from the helper. Refs: `annotator/annotation/event_mcp_server.py:1263-1269`.
- `High`: synthesized relation targeting is wrong once event IDs reach two digits. `_best_matching_procedure_event()` and `_best_matching_device_event()` use lexicographic `max(event_id)`, so `EV9` sorts after `EV10`. Relation synthesis will eventually point at the wrong "latest" event. Refs: `annotator/annotation/event_mcp_server.py:2131-2167`, `annotator/annotation/event_mcp_server.py:2171-2197`.
- `Medium`: normalization collapses accepted multi-anchor evidence for some event types. `_event_specific_anchor_selection()` reduces `history_fact` and `imaging_finding` events to one anchor during final normalization/merge. That breaks the clean-break rule that cross-sentence evidence must stay represented as multiple anchors. Refs: `annotator/annotation/event_mcp_server.py:893-902`, `annotator/annotation/event_mcp_server.py:905-952`.
- `Medium`: `_find_merge_candidate()` and `_merge_events()` can merge distinct facts and then lose information. Imaging findings are merged by only `(concept_id, laterality, location)` and history facts by only `(concept_id, subject)`, so distinct stones or history facts with different size, count, timeframe, or qualifier can collapse. `_merge_events()` then keeps only `left.relations`, dropping all relations on the merged-away event. Refs: `annotator/annotation/event_mcp_server.py:1104-1166`.
- `Medium`: `_expand_patient_report_anchor()` can jump to the wrong occurrence because the prefix fallback uses `raw_text.find(candidate)` across the entire note instead of searching near the original anchor. Refs: `annotator/annotation/event_mcp_server.py:1088-1101`.
- `Medium`: social-support value enrichment is inverted for common negative phrasing. In `social_context_fact`, the value regex checks `has` before `no`/`limited`, so text like `Patient has no transportation` resolves to `value="available"`. This was reproducible via `_enrich_event_attributes()`. Refs: `annotator/annotation/event_mcp_server.py:1869-1885`.
- `Medium`: the server extracts medication-related adverse-effect causes but never auto-links them. `_enrich_event_attributes()` emits `suspected_cause="opioid"` or `suspected_cause="antibiotic"`, but `_synthesize_default_relations()` only links procedures and devices. Medication-caused reactions therefore need manual linking even though the server inferred the cause class. Refs: `annotator/annotation/event_mcp_server.py:1850-1868`, `annotator/annotation/event_mcp_server.py:2143-2166`.
- `Medium`: `get_timeline_context()` slices before sorting and forces `limit >= 1`. If the loader is not already chronological, notes can be dropped before ordering, and `limit=0` still returns one note. Refs: `annotator/annotation/event_mcp_server.py:720-738`.
- `Medium`: `submit_gap()` accepts blank gap concepts and stores raw anchors without canonicalization. That permits meaningless gap records and leaves duplicate/overlapping anchor cleanup inconsistent with event handling. Refs: `annotator/annotation/event_mcp_server.py:656-705`.

Suggestions:
- Require `plan_note()` before any mutating tool and fail closed otherwise.
- Reject partial anchor resolution explicitly and include the unresolved quotes in the error.
- Heartbeat the lock in `complete_family()` and `finalize_note()`.
- Stage finalized events/projections in locals and commit them to session state only after a successful save.
- Preserve multi-anchor evidence through normalization for all event types.

## `event_session.py`

- `Medium`: family completion ignores `review_status`. Any status, including `needs_review` or an empty string, clears `pending_families()` because completion is keyed only by family presence. This was reproducible directly on `EventAnnotationSession`. Refs: `annotator/annotation/event_session.py:62-70`.
- `Medium`: request-id caching is unsafe because the cache key is only `tool_name:request_id`. Reusing a request ID with a different payload returns a stale success response and suppresses the new operation. Refs: `annotator/annotation/event_session.py:34-38`.
- `Low`: `_request_cache` is unbounded for the life of the session. Long sessions with retries or orchestrator loops can accumulate stale entries indefinitely. Refs: `annotator/annotation/event_session.py:30`, `annotator/annotation/event_session.py:34-38`.

Suggestions:
- Count only `review_status == "reviewed"` as complete.
- Include a payload hash in the idempotency key.
- Bound or periodically prune the request cache.

## `event_store.py`

- `High`: corrupted artifacts are fatal to runtime operations. `load()` blindly `json.load()`s with no recovery path, and callers do not catch exceptions. One malformed artifact can block reopening a note or loading a patient timeline. Refs: `annotator/annotation/event_store.py:21-25`, `annotator/annotation/event_mcp_server.py:228-237`, `annotator/annotation/event_mcp_server.py:732-734`.
- `Medium`: artifact schema/version is not validated on load. Old or hand-edited artifacts are hydrated directly into session state, so shape drift is detected only later and indirectly. Refs: `annotator/annotation/event_store.py:21-25`, `annotator/annotation/event_store.py:27-50`.
- `Low`: `save()` uses atomic replace but not `fsync()` on the temp file or parent directory. That is usually acceptable, but it is not crash-durable in the strict sense. Refs: `annotator/annotation/event_store.py:47-50`.
- `Low`: `event_artifact_path()` uses raw IDs as path components without sanitization. If note IDs or patient IDs ever contain separators, artifacts can escape the intended directory layout. Refs: `annotator/annotation/event_store.py:58-59`.

Suggestions:
- Catch and quarantine malformed artifacts instead of hard-failing.
- Validate `schema_version` and the top-level shape on load.
- Sanitize or encode path components if IDs are not guaranteed safe.

## `event_planner.py`

- `High`: `_is_historical_context()` over-suppresses current stone mentions after any nearby `problem list` phrase. `build_note_plan("OUT", "PROBLEM LIST: HTN. TODAY: CT shows right ureteral stone with hydronephrosis.", [])` currently returns `irrelevant`, because the function immediately returns `True` whenever `problem list` appears in the prior 600 characters. Refs: `annotator/annotation/event_planner.py:462-466`.
- `Medium`: indication-only mentions can activate the wrong families. `_keyword_present()` suppresses indication-context matches only for `symptoms_imaging` and `procedure_devices`, not for `medications` or `outcomes_complications`. Reproduced: `INDICATION: fever ... FINDINGS: 5 mm right ureteral stone with hydronephrosis` activates `outcomes_complications` solely from the indication text. Refs: `annotator/annotation/event_planner.py:317-356`, `annotator/annotation/event_planner.py:409-438`, `annotator/annotation/event_planner.py:493-501`.
- `Medium`: negation handling is one-sided. `_pattern_present()` and `_keyword_present()` only inspect the 48 characters before a match. Post-term negation or resolution phrases like `flank pain absent`, `hematuria resolved`, or `fever free` still count as active signals. Refs: `annotator/annotation/event_planner.py:365-380`, `annotator/annotation/event_planner.py:409-438`.
- `Low`: planner hot paths do avoidable repeated work. Every note recompiles keyword regexes and linearly scans the section map for each match. Refs: `annotator/annotation/event_planner.py:239-259`, `annotator/annotation/event_planner.py:441-453`.

Suggestions:
- Narrow `problem list` suppression to actual problem-list sections, not any nearby substring.
- Apply indication suppression consistently across all families.
- Add post-term negation/resolution patterns.
- Cache compiled keyword regexes and, if needed, use a faster section lookup.

## `evidence_retriever.py`

- `High`: the retriever drops clinically important short abbreviations. `_tokenize()` and `_normalize_token()` discard tokens shorter than 3 characters, so searches for `CT`, `US`, `ED`, `ER`, or `JJ` return no results even when the text is present. Reproduced directly with `NoteEvidenceRetriever("Patient returned to ED. CT showed 6 mm stone. US later confirmed hydronephrosis.").search(["CT"]) == []`. Refs: `annotator/annotation/evidence_retriever.py:11`, `annotator/annotation/evidence_retriever.py:143-156`.
- `Medium`: phrase ranking is line-centric and does not bridge line breaks. Token hits are collected per line, and the phrase bonus only fires when the full phrase occurs inside one line, so split-line evidence is easy to underrank. Refs: `annotator/annotation/evidence_retriever.py:44-84`, `annotator/annotation/evidence_retriever.py:119-140`.
- `Low`: phrase scoring loops over every line for every multi-word phrase and repeatedly `casefold()`s the same text. This is easy constant-factor overhead to remove with cached lowercase lines. Refs: `annotator/annotation/evidence_retriever.py:73-84`.
- `Low`: redundancy detection is asymmetric because overlap is divided only by the candidate window length. Large later windows can survive even when they mostly subsume earlier smaller windows. Refs: `annotator/annotation/evidence_retriever.py:158-170`.

Suggestions:
- Keep a whitelist of clinically important short tokens instead of globally dropping `<3` character terms.
- Consider sentence-based or sliding-span retrieval for phrases that cross line breaks.
- Cache lowercase line text for phrase scoring.

## `event_projector.py`

- `High`: projection matching ignores polarity and temporality. `_match_events()` does not filter `assertion_strength`, `temporal_status`, or `experiencer`, so negated or historical events still satisfy note-scoped resolvers. Reproduced: a `NEGATED` hydronephrosis event answers `Was hydronephrosis present?` as `True`. Refs: `annotator/annotation/event_projector.py:12-36`, `annotator/annotation/event_projector.py:107-126`.
- `Medium`: `event_attribute_values` credits non-contributing events in `supporting_event_ids`. `_collect_attribute_values()` correctly ignores matched events that lack the requested attribute, but `_run_resolver()` still reports all matched event IDs as support. This was reproducible with one symptom event that had `symptoms` and one that did not. Refs: `annotator/annotation/event_projector.py:83-102`, `annotator/annotation/event_projector.py:129-147`.
- `Medium`: unknown resolver IDs fail silently. `_run_resolver()` returns `None` for unexpected IDs, and `project_note_answers()` drops the registry row without surfacing any projection error. Refs: `annotator/annotation/event_projector.py:32-35`, `annotator/annotation/event_projector.py:39-104`.
- `Medium`: impossible attribute resolvers degrade to permanent `unanswered` with no diagnostics. When the registry points at an inaccessible attribute key, projection quietly stays empty forever. Refs: `annotator/annotation/event_projector.py:83-102`.

Suggestions:
- Exclude `NEGATED` events by default for note-presence/value questions.
- Build `supporting_event_ids` from the events that actually contributed values.
- Surface bad resolver configurations explicitly instead of dropping them silently.

## `terminology.py`

- `Medium`: the source contains conflicting duplicate aliases, so canonicalization depends on source order. In the `procedure_devices/device_event` table, `nephroureteral stent` appears twice with different canonical targets: once as `nephrostomy tube`, later as `nephroureteral stent`. The later entry silently wins at import time. Refs: `annotator/annotation/terminology.py:350`, `annotator/annotation/terminology.py:388`.
- `Medium`: the exposed medication `drug_class` vocabulary is incomplete relative to the values the server emits. `event_mcp_server._enrich_event_attributes()` can output `non-opioid analgesic`, `adjuvant analgesic`, `cystine stone therapy`, `5-alpha reductase inhibitor`, `local anesthetic`, `antihistamine`, `alkalinization therapy`, and `anti-gout`, but none of these appear in the terminology summary for `medications.medication_exposure.drug_class`. Family bundles therefore hide valid server-produced values from the agent. Refs: `annotator/annotation/terminology.py:1137-1164`, `annotator/annotation/event_mcp_server.py:1622-1640`.
- `Medium`: `get_family_terminology_summary()` exposes only attributes backed by `_ATTRIBUTE_TERMS`. Compared against `FAMILY_SPECS`, 43 optional attributes currently have no surfaced controlled-value summary, including `clinical_context`, `procedure_date`, `suspected_cause`, `comparison_status`, and `recommendation_event.status`. That makes the family bundle materially less useful as the controlled-vocabulary contract. Refs: `annotator/annotation/terminology.py:1763-1800`.
- `Low`: normalized lookup tables are rebuilt on every call. `canonicalize_event_term()` reconstructs a normalized event alias map each time, and `canonicalize_attributes()` rebuilds a normalized alias map for every attribute on every call. Refs: `annotator/annotation/terminology.py:1753-1782`.

Suggestions:
- Remove conflicting duplicate aliases and add a duplicate-key lint check.
- Precompute normalized alias maps once at import time.
- Either surface all controlled attributes in family bundles or explicitly mark which attributes are intentionally free-text/numeric.

## `clinician_registry.py`

- `High`: several rows are marked `implemented` even though the runtime cannot satisfy them. From the current workbook, at least seven implemented resolvers are structurally impossible:
- `What was the length of the stent?`
- `What was the diameter of the stent?`
- `What was the diameter of the balloon dilation?`
- `What was the laser setting power?`
- `What was the laser fiber type?`
- `What was the ureteroscope type?`
- `Did the patient experience an anesthetic complication?` because it filters on nonexistent adverse-effect `context`
- These are emitted by `_infer_resolver()` today. Refs: `annotator/annotation/clinician_registry.py:243-315`.
- `High`: the hematoma resolver is implemented in the wrong family. `Was a hematoma present after surgery` is assigned to `procedure_devices` and resolves by concept keyword `hematoma`, but hematoma lives under `outcomes_complications/adverse_effect`, so the projection can never match extracted events. Refs: `annotator/annotation/clinician_registry.py:170-173`, `annotator/annotation/clinician_registry.py:253-254`.
- `Medium`: family inference is too narrow for the actual workbook. Loading the current spreadsheet yields 42 `unclassified` rows, including actionable note/hybrid items such as `Which family members have a history of kidney stones?`, `Did the patient experience urinary retention?`, and `How was the patient positioned?`. Refs: `annotator/annotation/clinician_registry.py:164-186`.
- `Medium`: resolver heuristics miss obvious wording variants from the workbook. Example: `_infer_resolver()` checks `make/model of ureteroscope`, but the workbook row is `make / model of ureteroscope`, so it falls through to deferred. Refs: `annotator/annotation/clinician_registry.py:280-284`.
- `Medium`: education/profession/employment questions are classified into `history_timeline` but still fall through to deferred, because the non-family-specific resolver logic only handles `caregiver` and `transportation`. Refs: `annotator/annotation/clinician_registry.py:126-130`, `annotator/annotation/clinician_registry.py:182-183`, `annotator/annotation/clinician_registry.py:317-320`.
- `Medium`: workbook loading is fragile because it hardcodes sheet names and positional columns without validating headers. If the spreadsheet layout drifts, the loader can silently return a partial or misaligned registry or raise an index error deep in row parsing. Refs: `annotator/annotation/clinician_registry.py:22-30`, `annotator/annotation/clinician_registry.py:43-58`, `annotator/annotation/clinician_registry.py:62-89`.
- `Low`: `_infer_source_class()` is brittle because any HR comment starting with `no` is treated as `unanswerable_now`, even if the rest of the comment is more nuanced. Refs: `annotator/annotation/clinician_registry.py:104-130`.

Suggestions:
- Add a post-load validation pass that checks every implemented resolver against the runtime event schema.
- Promote unclassified/planned rows into an explicit review queue instead of letting them silently drift.
- Validate workbook headers and required sheets before reading row data.
- Replace exact-string resolver heuristics with a normalized question-pattern layer.

## Recommended Test Additions

- `event_mcp_server`: finalizing without calling `plan_note()` should fail closed.
- `event_mcp_server`: submitting two evidence quotes where one does not resolve should reject the request.
- `event_mcp_server`: corrupt artifact JSON should not strand the note lock.
- `event_mcp_server`: `without complication` should not force `assertion_strength="NEGATED"`.
- `event_mcp_server`: relation synthesis should choose `EV10` over `EV9`.
- `event_mcp_server`: multi-anchor evidence should survive `finalize_note()` for imaging and history events.
- `event_mcp_server`: `Patient has no transportation` should not normalize to `value="available"`.
- `event_planner`: a nearby `problem list` mention should not suppress a current stone finding in the same note.
- `event_planner`: indication-only fever/medication mentions should not activate `outcomes_complications` or `medications`.
- `evidence_retriever`: short clinical abbreviations like `CT`, `US`, `ED`, and `JJ` should retrieve hits.
- `event_projector`: negated events should not answer note-presence questions as positive.
- `event_projector`: `supporting_event_ids` for attribute-value questions should include only contributing events.
- `clinician_registry`: implemented resolvers should fail tests when they reference attributes the extraction runtime cannot store.

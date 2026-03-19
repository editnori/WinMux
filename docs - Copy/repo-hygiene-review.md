# Repo Hygiene Review Ledger

This ledger records the maintainability sweep performed during the repo-wide hygiene campaign. It focuses on reviewable findings, what was fixed in this pass, and what still merits future decomposition.

## Runtime

- Fixed duplicated note/procedure date parsing by extracting `annotator/annotation/core/date_utils.py` and reusing it from the MCP runtime and `event_projector.py`.
- Fixed the live request-dedupe bug in `annotator/annotation/core/infra/event_session.py` by making cache hits payload-aware and bounding the in-session request cache.
- Split deterministic relation synthesis out of the MCP runtime into `annotator/annotation/core/event_relations.py`.
- Split graph normalization, anchor cleanup, and assertion/temporal normalization out of the MCP runtime into `annotator/annotation/core/event_graph_normalization.py`.
- Replaced the inline concept-anchor keyword conditional block with the table-driven helper in `annotator/annotation/core/event_concept_keywords.py`.
- Split deterministic attribute enrichment out of the MCP runtime into `annotator/annotation/core/enrichment/dispatcher.py`, then extracted the largest imaging and medication branches into dedicated modules plus `annotator/annotation/core/enrichment/support.py`.
- Reduced the MCP entrypoint to a thin facade in `annotator/annotation/core/mcp/server.py` and split the remaining server code along runtime seams into `state.py`, `read_api.py`, `mutations.py`, and `finalize.py`.
- Kept request payload shaping and the smallest mutation-validation helpers local to `annotator/annotation/core/mcp/mutations.py` after review; splitting those further added file count without reducing real coupling.
- Finalized the remaining high-churn implementation areas into subpackages: `core/mcp/`, `core/enrichment/`, `core/ontology/`, and `core/registry/`, and removed the temporary top-level compatibility shims after the import cutover landed.
- Split planner regex/config declarations out of the main planner into `annotator/annotation/core/planning/patterns.py`.
- Confirmed the public runtime surface now sits behind a much smaller compatibility facade; remaining runtime complexity lives in focused state, read, mutation, and finalize modules instead of one monolith.
- No remaining high-severity runtime defects were found after the cache fix, helper extraction, and regression test pass.

## Catalog / Registry

- Regenerated the checked-in question catalog outputs from live code and the current clinician-intent import state.
- Fixed `annotation.tools.question_catalog.write_question_catalog_report()` so JSON and Markdown outputs both create parent directories before writing.
- Split resolver heuristics out of `annotator/annotation/core/registry/loader.py` into `annotator/annotation/core/registry/resolvers.py`.
- Split terminology vocab data out of the normalization API into `annotator/annotation/core/ontology/event_terms.py` and `annotator/annotation/core/ontology/attribute_terms.py`, leaving `annotator/annotation/core/ontology/terminology.py` as the normalization surface.
- Confirmed the current generated catalog state is `216` clinician-intent catalog entries, `130` `Sheet1` note-oriented questions, `112` event-backed questions, `18` timeline-backed questions, and `0` orphan event types.

## Planner

- Split planner regex/config declarations into `annotator/annotation/core/planning/patterns.py`, reducing the main planner module’s inline pattern mass while preserving the current behavior and helper flow.
- `annotator/annotation/core/planning/planner.py` still contains suppression and decision logic in one file; the next clean split would be moving the suppression/match helpers behind a dedicated planner-matching module.

## Tooling / Scripts

- Regenerated stale derived artifacts so checked-in docs and reports match live tooling output.
- Reviewed CLI entrypoints under `annotator/annotation/tools/` and removed the old `annotator/scripts/` wrapper layer after the maintained tool entrypoints fully covered those workflows. The main concrete fix in that pass was output-path creation in the question catalog generator.
- Split question catalog corpus counting and Markdown rendering into `annotator/annotation/tools/question_catalog_corpus.py` and `annotator/annotation/tools/question_catalog_render.py` while preserving the existing CLI surface in `question_catalog.py`.
- `annotator/run_event_mcp.py` remains the preferred stable launcher for local MCP use.

## Tests

- Added a regression test covering question catalog report generation when the Markdown output directory does not already exist.
- Added focused tests for the shared date parser so the extracted helper is covered independently of the larger runtime modules.
- Added focused tests for extracted relation synthesis and resolver inference modules.
- Added direct tests for the extracted attribute enrichment module and kept the ontology-consistency AST guard pointed at the live enrichment implementation.
- Added direct tests for session request caching, note loading, note selection, graph normalization, concept keyword tables, and low-risk review/audit tools.
- Added an integration regression test proving `request_id` dedupe is now payload-sensitive in the live MCP runtime.
- Full annotation test suite remained green after cleanup.

## Docs / Generated Artifacts

- Updated stale generated coverage docs to match the live catalog output.
- Updated top-level onboarding docs to use current runtime paths and the launcher entrypoint instead of pre-cleanup module paths.
- Residual historical references still exist in `docs/archive/`; those were left untouched because they are archival rather than active onboarding material.

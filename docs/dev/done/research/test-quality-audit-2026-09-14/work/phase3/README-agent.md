# Phase 3 triage agent instructions (coverage-opportunity analysis)

Audit worktree root (ONLY tree you may read or write): C:\Users\vlad3\Documents\Code\Parsek\Parsek-test-audit
Never read C:\Users\vlad3\Documents\Code\Parsek\Parsek or any other Parsek-* sibling. No git, no dotnet, no .cs edits.
Read `docs\dev\research\test-quality-audit-2026-09-14\rubric.md` and `docs\dev\plans\test-quality-audit.md` (section "Phase 3") first.

Input: your partition file `work\phase3\candidates-<partition>.jsonl` (one coverage candidate per line: cand_id, sut, sut_file, sut_line, case_kind, proposed_name, sketch, guards, risk, batch, cluster).
Also available for cross-reference: `work\phase3\medium-high-T3.jsonl` (Medium/High T3 findings whose proposed_action implies a new or strengthened test) and `coverage-by-class.csv` (coverlet per-class line/branch rates at the baseline).

For EACH candidate:
1. Open the SUT at `Source\Parsek\<sut_file>` around `sut_line` and confirm the guard/branch exists at this SHA (correct the line if it moved by a few lines; mark `invalid` if it does not exist).
2. Check headless feasibility: can an xUnit test in Source\Parsek.Tests reach it without Unity/KSP runtime objects (Vessel, Part, Transform, FlightGlobals)? Use the existing generators (Source\Parsek.Tests\Generators: RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter) and existing test patterns in the same area. Classify feasibility: `direct` (callable now), `seam` (needs a small internal static extraction or injectable seam - name it), `generator` (needs a generator/fixture change - name it), `in-game` (only an InGameTest can reach it).
3. Check it is not already covered: grep Source\Parsek.Tests for the SUT symbol; if an existing test already pins the exact branch, mark `already-covered` and name the test.
4. Name the mutant: the one-line production change that the proposed test would red on (this is mandatory; a proposal without a mutant is dropped).
5. Estimate effort S/M/L and assign a priority score: risk order data-loss > career > recording > playback > ui, then feasibility (direct > seam > generator > in-game), then whether a Medium/High finding already names the gap.

Merge duplicates across candidates that target the same SUT guard (keep one, list the merged cand_ids).

Output 1: `work\phase3\ranked-<partition>.jsonl`, one line per kept proposal:
{"cand_id":..., "merged":[...], "cluster":..., "risk":..., "sut":..., "sut_file":..., "sut_line":N, "case_kind":..., "proposed_name":..., "feasibility":"direct|seam|generator|in-game", "seam_or_generator":"...or empty", "mutant":"...", "effort":"S|M|L", "priority":1-5 (1 highest), "related_finding":"F-... or none", "status":"keep|already-covered|invalid", "note":"short"}
Output 2: `work\phase3\ranked-<partition>.md`: per cluster (cap 10 kept proposals per cluster, highest priority first), a table with cand_id, SUT file:line, case kind, feasibility, mutant, effort, priority. Plain ASCII, LF, no em dashes.

Return at most 12 lines: counts kept / already-covered / invalid / merged per partition; the top 5 proposals by priority with one line each; blockers.

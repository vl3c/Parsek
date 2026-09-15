# Phase 1 summary - full-file review (closed 2026-09-15)

Baseline `4aedb0a1a`. Every one of the 1,010 test files got an agent pass under the frozen rubric
and protocol (`rubric.md`, `tools/agent-protocol.md`); 426 manifest batches plus `supplement-001`
(the 7 TRX-only methods the scanner missed, see `work/inventory-completeness.txt`). All 427
fragments pass `tools/lint_fragments.py`; `merge_fragments.py` filled every D1 row.

Review agents: Opus 5 (High effort), 4 in parallel, 2-8 batches per pass sized by tier; the
session (Fable 5.1) supervised, fixed one lint failure left by a stalled pass (ledger-career-034,
two rows missing the `->` token), and spot-checked 100% of High findings (`work/supervisor-notes.md`, committed).

## Totals

(Counts as returned by Phase 1; Phase 2 later downgraded F-recording-tree-003-02 from Medium to Low, so the committed registers read Medium 272 / Low 489.)

- Method rows: 20225 (= TRX executed-method count); ok 18922 (93.6%).
- Verdicts: {'ok': 18922, 'duplicate': 348, 'vacuous': 258, 'source-gate-ok': 240, 'weak': 216, 'misleading': 181, 'source-gate-misplaced': 24, 'fixture-limited': 18, 'organization': 11, 'brittle': 7}
- Findings: 768 - by severity {'Medium': 273, 'Low': 488, 'High': 7}; by category {'T1': 185, 'T3': 321, 'T6': 5, 'T4': 25, 'T2': 232}. No Critical.
- Coverage candidates: 252 - by risk {'recording': 103, 'career': 54, 'playback': 57, 'data-loss': 12, 'ui': 26}; by case kind {'state-transition': 44, 'negative': 89, 'boundary': 55, 'integration': 29, 'mirror-direction': 25, 'serializer-key': 6, 'roundtrip': 4}.

## Per cluster

| Cluster | Methods | ok | non-ok | Findings (High/Med/Low) |
|---|---|---|---|---|
| rewind-refly | 919 | 857 | 62 | 0/18/20 |
| recording-tree | 2340 | 2093 | 247 | 2/55/89 |
| ledger-career | 1853 | 1747 | 106 | 0/35/47 |
| recorder-events | 1489 | 1363 | 126 | 3/17/53 |
| logistics-route | 1871 | 1813 | 58 | 0/16/30 |
| spawn-vessel | 977 | 918 | 59 | 1/8/26 |
| ghost-playback | 1449 | 1356 | 93 | 0/21/16 |
| map-render | 1357 | 1246 | 111 | 0/18/28 |
| trajectory-orbit | 819 | 767 | 52 | 1/10/26 |
| mission-groups | 735 | 708 | 27 | 0/10/6 |
| analyzer | 213 | 206 | 7 | 0/2/2 |
| logging | 186 | 170 | 16 | 0/1/8 |
| harness-seam | 1126 | 1102 | 24 | 0/6/14 |
| wiring-gates | 204 | 142 | 62 | 0/2/5 |
| ui-settings | 217 | 207 | 10 | 0/2/6 |
| io-serialization | 164 | 156 | 8 | 0/2/2 |
| legacy-bugfix | 1255 | 1159 | 96 | 0/14/41 |
| catchall | 3044 | 2907 | 137 | 0/23/56 |
| supplement | 7 | 5 | 2 | 0/0/2 |

## Stop-condition check

- Linter rejections: 0 of 427 fragments at return (one pre-existing failure repaired by the supervisor).
- Confirmed T1+T2 rate stayed well above the 5-per-1,000 switch-to-scanner threshold in every round
  (the 258 vacuous + 348 duplicate rows are 30 per 1,000), so no cluster was downgraded to scanner-only.
- Wall: rounds of 4 agents, 3-10 minutes per agent pass; no pass exceeded the two-hour cap.

## High findings (all 7 supervisor-confirmed T1, go to Phase 2 refutation + bounded mutation)

- F-recorder-events-003-01, F-recorder-events-013-01, F-recorder-events-024-01,
  F-recording-tree-050-04, F-recording-tree-050-07, F-spawn-vessel-004-01, F-trajectory-orbit-015-01.

## Recurring patterns (input to Phase 3 / Phase 4)

- Inline replay of a production branch inside the test body, then assertions on the test's own
  writes (the dominant T1 shape; legacy bug files and "simulated integration" cells).
- Source-text gates over unstripped source (`IndexOf` on raw file text, fixed char windows) where
  `SourceScanText.StripCommentsAndMaskLiterals` already exists in the test project (T4).
- "Culture-invariant" cells that install no culture, so the mutation reds only on a comma-decimal host.
- Single-branch predicate pins with no mirror case (Medium T3 bulk).
- Byte-identical twins distinguished only by name or comment (Low T2 bulk).

## Known gaps

- Verdict miss rate on Medium findings is ~10-20% per calibration; Phase 2 verifies High and a
  sampled 10% of Medium, and Phase 4 dedups cross-batch twins that name each other.
- `harness-seam-009` manifest omitted `TestCommandEvaExitTests.Standoff_NoOtherLoadedVessel_ReadsClear`;
  it is reviewed under `supplement-001`.

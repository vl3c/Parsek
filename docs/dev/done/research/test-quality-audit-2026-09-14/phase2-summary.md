# Phase 2 summary - adversarial verification (closed 2026-09-15)

Baseline `4aedb0a1a`. Mutations ran only in the detached scratch worktree `../Parsek-testaudit-mut`
(clean before and after every run; `git status --porcelain` empty), one filtered class per run,
never the full suite. Refutations of the Medium sample were read-only. Verdict rows:
`phase2-verdicts.csv`; patches and the run log: `mutations/`.

## High findings: 7 of 7 verified by mutation

Every mutation named by the finding left the whole filtered class GREEN at the baseline pass
count, so none of the seven tests can red on the production regression it is named for:

| Finding | Mutation | Class result |
|---|---|---|
| F-recorder-events-003-01 | stub `RemoveDuplicateCrewFromSnapshot` body | 164/164 green |
| F-recorder-events-013-01 | make the bug-285 parent continuation unconditional | 58/58 green |
| F-recorder-events-024-01 | comment out all 19 `Check*State` polls | 17/17 green |
| F-recording-tree-050-04 | reintroduce `VesselSnapshot = null` in the destroy handler | 12/12 green |
| F-recording-tree-050-07 | reintroduce `VesselSnapshot = null` in the EVA-boarding branch | 12/12 green |
| F-spawn-vessel-004-01 | short-circuit the `AdoptControllersIfEmpty` backstop | 40/40 green |
| F-trajectory-orbit-015-01 | feed the live anchor pose from `GetWorldPos3D` (the shipped drift bug) | 6/6 green |

## Medium sample: 27 of 273 (seeded 10%), read-only refutation

- 26 verified, 0 refuted, 1 downgraded to Low (F-recording-tree-003-02: the requested negative case
  already exists in the same file).
- 1 falsifiability string corrected (F-logistics-route-020-02: the named mutation reds a sibling
  cell, not this one; the finding stands with the corrected string).
- 1 wording over-reach noted, verdict unchanged (F-ghost-playback-008-02's "same shape" claim).
- Estimated Medium false-positive rate from the sample: 0/27 refuted, 1/27 severity drift.

## Exit

- 100% of High have a recorded mutation and verdict; no Critical exists.
- 7 of the 30-mutation cap used. Refuted items: none.
- Both adjudications are applied to the fragment rows (linted) and re-merged into `findings.csv`.

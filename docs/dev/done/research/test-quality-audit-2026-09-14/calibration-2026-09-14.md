# Calibration record - 2026-09-14

Baseline `4aedb0a1a`. Two pilot batches, three independent review passes each (r1/r2/r3), same
protocol and rubric. Purpose: measure agreement before the 426-batch fan-out and fix the protocol
while divergence is cheap.

## Agreement

| Batch | Methods | r1 vs r2 exact | r1 vs r3 exact | r2 vs r3 exact | Coarse (ok/gate/issue) |
|---|---|---|---|---|---|
| recording-tree-032 (EffectiveStateTests.cs, 39) | 39 | 94.9% | 92.3% | 92.3% | 92.3-94.9% |
| ghost-playback-024 (6 files, 51) | 51 | 86.3% | 84.3% | 96.1% | 92.2-96.1% |

Gate was >=80% pairwise agreement: PASS.

## Divergences and resolution

1. Weak vs vacuous (r1 called the GhostPlaybackLogHygiene direct-call cells `weak`, r2/r3 called
   them `vacuous`; all three found the same root cause: the production log sites are never
   executed). Rubric amended with an explicit boundary rule: if stubbing the named production line
   leaves the test green, it is `vacuous`; `weak` means the line is exercised but the assertion
   window is narrow.
2. r3 (recording-tree-032) missed the three Medium T3 findings on non-load-bearing fixtures that
   r1/r2 found. With 39-method batches the miss rate on Medium findings is ~10-20%; Phase 2
   verification and supervisor sampling cover this, and the per-method rows still exist.
3. All three runs independently found the same core defects (EffectiveState duplicate, LogHygiene
   direct-call replay x3, PlaybackOrbitDiagnostics over-determined guards, deployable stow baseline
   reading 0/3 regardless of the apply). Cross-run convergence on findings is high even where
   verdict labels differ.

## Protocol compliance findings

- The linter initially required `category` and falsifiability on every non-ok verdict, which
  wrongly rejected `source-gate-ok` rows; fixed (only actionable verdicts need findings).
- The linter initially required the literal `-> red`; agents legitimately write `-> green` for
  weak-assertion mutations and `-> both red` for duplicates. Relaxed to a non-empty falsifiability
  carrying `->`; content quality is verified in Phase 2, not by regex.
- One run used an out-of-enum coverage `case_kind` ("integration"); `integration` added as an
  allowed kind (it marks a gap that needs a live test, for Phase 3 triage).
- After the linter fixes, all six pilot fragments PASS. The protocol now requires every review
  agent to self-lint its fragments and paste the PASS lines before returning.

## Decision

Proceed to the full fan-out (426 batches, paired into 213 work orders, 8 parallel agents per
round). Pilot fragments stay under `findings/pilot/` as calibration evidence and are not merged;
the two pilot batches are re-run in the fan-out under the frozen protocol so the final dataset is
uniform.

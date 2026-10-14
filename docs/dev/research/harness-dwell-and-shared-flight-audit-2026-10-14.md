# Harness dwell and shared-flight audit (2026-10-14)

Status: PROPOSAL, partly applied. B1-B4 (the SAFE shared-flight merges) were accepted by
owner ruling B on 2026-10-14 and are implemented on branch `shared-flight-merges` (no flight
yet; each host owes one confirm flight). Everything else below still waits for an owner
decision, per the 2026-10-14 rulings:

- (A) dwell lanes: "audit, then propose cuts".
- (B) lanes that fly the same mission shape and only assert different things: "audit and
  propose" which groups could share one flight.

Classification used throughout:

- **SAFE** - no gating expectation reads the portion being cut (or, for a merge, every
  absorbed expectation was evaluated mechanically against the host lane's archived run and
  holds).
- **SAFE\*** - no armed value is expected to move, but a gated facet (the anomaly sweep, a
  render-composition floor) is accumulated over the whole dwell, so one confirming flight is
  owed before the change is relied on.
- **NEEDS-RE-PIN** - a count / duration / name pin moves and must be re-derived.
- **NO** - the dwell (or the separate flight) is the thing being tested, or the lanes'
  worlds conflict.

Evidence sources: the machine-wide archive `automation/results-archive/<runId>/` (mission
JSON `warpUtilisation` rows, mission stdout, `KSP.log.gz`, run JSON verifier rows),
`harness/coverage/duration.json` (p50), and `harness/tools/flight_efficiency.py --since
2026-10-11` run from `Parsek-flight-efficiency` (HEAD `7cbe594c0`). Spec line numbers are
against `origin/main` `e58528a3e`.

## 1. Summary of proposals

Seconds are wall seconds saved per run of that lane, taken from the latest archived run
unless marked p50.

| # | Lane(s) | Tier | Change | Saves (s) | Class |
|---|---------|------|--------|-----------|-------|
| A1 | V24W-duna-one-warp-stair | operator | `dwellHoldSeconds` 900 -> 120 | ~2,340 | SAFE\* |
| A2 | V3F-flight-arrival-faithful | operator | `dwellLeadSeconds` 120 -> 10, `dwellHoldSeconds` 300 -> 20 | ~840 | SAFE |
| A3 | V3R-flight-arrival-reaim | operator | same as A2 | ~840 | SAFE |
| A4 | V2-loop-arrival-dwell | operator | `dwellLeadSeconds` 120 -> 30, `dwellHoldSeconds` 300 -> 90 | ~630 | SAFE\* |
| A5 | V1-map-dwell-mun-orbit | nightly | `soiDwellLeadSeconds` 300 -> 60, `soiDwellTrailSeconds` 300 -> 60 | ~480 | SAFE\* |
| A6 | 11 kx_rewind_watch lanes with a PLAYBACK-WAIT | 4 nightly, 7 operator | `playbackMarginSeconds` 30 -> 15 | ~15 per wait (~75 nightly, ~105 operator) | SAFE\* (low value) |
| A7 | GS-2 / GS-3 / RF-18 / RF-20 / ST-2 | 4 operator, 1 nightly | `postDeployDwellSeconds` 45 -> 20 | ~25 each | SAFE\* (low value) |
| A8 | B13 / B14 landed dwell | nightly | none | 0 | NO |
| A9 | GS-7 PLAYBACK-WAIT (349 s) and the kx replay waits generally | - | none | 0 | NO |
| B1 | GS-4 absorbed into GS-9 | nightly | retire GS-4's flight, carry its expectations on GS-9 | ~325 (p50 338) | SAFE; implemented on branch shared-flight-merges |
| B2 | B2-lko-ascent absorbed into PWR-2 | nightly | carry B2's expectations on PWR-2 | ~272 | SAFE; implemented on branch shared-flight-merges |
| B3 | CL-1 absorbed into CL-2 | nightly | carry CL-1's expectations on CL-2 | ~154 | SAFE (reverses a recorded design choice); implemented on branch shared-flight-merges |
| B4 | CL-3 absorbed into CL-4 | operator | carry CL-3's expectations on CL-4 | ~64 | SAFE; implemented on branch shared-flight-merges |
| B5 | B17 absorbed into V3C | nightly | carry B17's expectations on V3C | ~810 | NEEDS-RE-PIN + harvest-source caveat |
| B6 | B11 absorbed into V1 | nightly | carry B11's expectations on V1 | ~715 | NEEDS-RE-PIN + harvest-source caveat |
| B7 | RF-18 into RF-20, CI-9 into VN-10, RF-1 into RF-4, VN-2 into VN-9 | mixed | - | - | NO (checked, conflicts) |

Totals if every SAFE and SAFE\* row is accepted (A1-A7, B1-B4):

- **nightly**: A5 480 + A6 ~75 + A7 ~25 + B1 325 + B2 272 + B3 154 = **~1,330 s (~22 min)
  per nightly tier run**. B5 + B6 would add ~1,525 s if their re-pins and the harvest
  question are accepted.
- **operator**: A1 2,340 + A2 840 + A3 840 + A4 630 + A6 ~105 + A7 ~100 + B4 64 = **~4,920 s
  (~82 min)** per full operator pass.
- **daily**: nothing found. No daily lane is dwell-dominated and no daily lane shares a
  mission + fixture with another lane.

## 2. Dwell lanes (ruling A)

How the set was found: every latest archived run's `warpUtilisation` phase row whose name
reads HOLD / DWELL / WATCH / PARK / SETTLE / WAIT / POST and whose game-seconds per wall
second is under 3 (i.e. a real-time phase), plus the `flight_efficiency` BY DESIGN `dwell`
rows. The m3 render holds dominate: `flight_efficiency` lists every V2 / V3F / V3R hold as
`byDesign 291 s ... m3 render hold: a deliberate 1x hold of dwellHoldSeconds game time for
render observation; shorten it rather than warp`. V24W does not appear in the BY DESIGN
table at all because its holds classify as `atmo/ground` (the parked fixture vessel is
landed), and V1's 600 s crossing classifies the same way; both were found from the
phase rows instead.

### A1. V24W-duna-one-warp-stair (operator, 2,868 s)

Where the wall goes (run `2026-10-12_2211`, mission wall 2,819 s): ARM 1 s, CAMERA 0.6 s,
three TimeJump legs 1.5 s each, and three windows HOLD-DEPART 939 s / HOLD-ARRIVE 937 s /
HOLD-PARK 938 s. Each window is the stair (7 rungs x 10 frames x 0.5 s = ~35 s wall,
~6,700 game s) followed by a 900 game-second 1x hold (`V24W...toml:600`). So ~2,700 s of
the run is the three 1x tails.

What the gates read:

- `[expectations.renderComposition]` (`:715`, gating): `dwells {1,32}` (`:874`),
  `unevaluable {max 500000}`, `warpBuckets ["warp100","warp1000"]` (`:876`),
  `requireSeamKinds ["rigid","flexible-soi"]` (`:877`). With `warpBuckets` declared,
  `seamsAboveOneX == 0` and `holdsAboveOneX == 0` are FAIL findings. Observed on the
  latest run: dwells 2 (+2 open), warp100 10,610, warp1000 2,170, warp1x 321,964, seam
  kinds rigid 11 / flexible-soi 4, holdsAboveOneX 1, unevaluable 334,236.
- The spec header says in its own words that the 1x tail is not this lane's contribution:
  `warp1x` is deliberately NOT required because "requiring it would pin the 1x HOLD that
  follows each stair, which is V2's shape and not this lane's contribution". The above-1x
  buckets and the seam traversals come from the stair, which crosses each window instant
  at rails ("The stair therefore CROSSES each window instant at rails - which is the
  point").
- `allowedAnomalies` tolerates icon-teleport / icon-off-orbit / loop-seam-teleport bare;
  any other gated anomaly anywhere in the run reds.

Proposal: `dwellHoldSeconds` 900 -> 120 (keep `dwellLeadSeconds = 300`, the stair needs it;
`holdTimeoutSeconds = 1800` still exceeds the hold). Saves 3 x 780 = ~2,340 s; the run drops
to ~530 s.

Risk, why SAFE\* and not SAFE: the closed `dwells` count and the `unevaluable` total are
accumulated over the whole session and I did not establish from the manifest which event
closes a dwell (the archive keeps no render manifest). If a dwell closes only at a leg
TimeJump (two legs -> two closed dwells, which matches the measured 2), the hold length is
irrelevant; if one closes inside a 1x tail, a shorter tail could still close it. The floor
is `min = 1`, so even one closed dwell passes. The RC-QUAL `observed holds` INFO (7,039 s)
would shrink by up to ~780 s; it is INFO, not gated. One confirming flight owed.

### A2 / A3. V3F-flight-arrival-faithful and V3R-flight-arrival-reaim (operator, ~950 s each)

Where the wall goes (runs `2026-10-12_2305` V3F and `2026-10-12_2321` V3R, mission wall
908 s each): HOLD-DEPART 301 s, HOLD-ARRIVE 301 s, HOLD-PARK 302 s; everything else under
2 s per phase. ~95 % of each run is the three 300 game-second 1x holds
(`V3F...toml:78-79`, `V3R...toml:57-58`).

What the gates read:

- Required tokens (`V3F:130`, `V3R:101`): `missionconfig applied:`, `SetLoopEnabled: loop
  ON`, `[ReaimDiag] mission=`, and `FORCED FAITHFUL` (V3F) / the `ENGAGED re-aim` line
  (V3R). All four fire at ARM time, before the first hold.
- Forbidden: `[Parsek][ERROR]` plus the opposite mode's line.
- Save blocks (rewind rows zero, one committed tree) read the final save, not the holds.
- `allowedAnomalies = []` sweeps the whole run.
- The holds themselves are documented as VACUOUS for the question they were built for: the
  spec headers state the flight-engine loop ghost is hidden by zone from a parked observer
  so no seam instrument can fire. Re-measured on the latest runs: `GuardSkip ...
  reason=hidden-by-zone` 899 (V3F) / 897 (V3R) lines, `mission-loop-unit-inactive` 899 /
  897, and the only `[ReaimSeam]` lines are `map effective segments` / `map orbit
  applied` (map proto path), not the flight-engine seam instrument. The engine path these
  lanes exist to drive never positions the ghost during any hold.

Proposal: `dwellLeadSeconds` 120 -> 10 and `dwellHoldSeconds` 300 -> 20 on both. The
`dwelledAllWindows` mission assertion still needs all three windows stamped, which a 20 s
hold does. Saves ~3 x 280 = ~840 s per lane; each run drops to ~110 s. SAFE: nothing gated
reads the hold contents, and the anomaly sweep over a hidden ghost has nothing to sample.
The cut should be revisited if a future change (the V3C companion shape) makes the parked
holds non-vacuous.

### A4. V2-loop-arrival-dwell (operator, 953 s)

Where the wall goes (run `2026-10-12_2146`, mission wall 909 s): HOLD-DEPART 301 s,
HOLD-ARRIVE 301 s, HOLD-PARK 302 s (`V2...toml:107,111`).

What the gates read (`V2...toml:180-196`):

- `missionconfig applied:`, `SetLoopEnabled: loop ON`, `[ReaimDiag] mission=`, and the
  `ENGAGED re-aim` line: all at ARM (KSP.log 00:47:12, before the first jump).
- `probe frame summary ... ghosts=[1-9]`: first fires 2 s after the depart jump
  (00:47:16); 181 such lines in the run.
- The ARRIVAL pin `phase=body-orbit surface=ProtoOrbitLine ... body=Duna`: fires ONCE
  (00:54:17), at `currentUT=28781184.562`, i.e. ~1 s before the arrive window instant
  (HOLD-ARRIVE started at `ut=28781065.542` = window - 120). The Sun handoff line fires the
  same way at the depart window instant (00:49:14, 120 s into HOLD-DEPART).
- `allowedAnomalies = []` (`:141`) sweeps the whole run; the header calls it the armed
  regression floor and says "do NOT relax a gate on a red".
- Nothing gated is specific to the PARK window or to any frame later than ~1 s after a
  window instant.

Proposal: `dwellLeadSeconds` 120 -> 30, `dwellHoldSeconds` 300 -> 90, so each hold spans
[window - 30, window + 60]. That keeps the header's "the hold must SPAN the window" rule
(flight 5's lesson) and keeps both handoff lines inside the hold. Saves 3 x 210 = ~630 s.

Risk, why SAFE\*: the post-crossing tail shrinks from 180 to 60 game s, so the clean anomaly
sweep watches fewer frames after each handoff. No pin moves; the sweep's coverage does.
The m3 machine takes ONE `dwellHoldSeconds` for all three windows
(`harness/missions/lib/mlib.py:23613-23614`), so a "short PARK only" variant would need a
mission change and is not proposed.

### A5. V1-map-dwell-mun-orbit (nightly, 1,440 s)

Where the wall goes (run `2026-10-12_0859`, mission wall 1,386 s): FLIGHT 667 s (the B11
profile), STOP / IDLE / REWIND / VERIFY ~6 s, DWELL-CAMERA 0.6 s, DWELL-WARP-IN 9 s,
DWELL-HOLD-1X 45 s, DWELL-WARP-RAMP 37 s, DWELL-SOI-WARP 22 s, **DWELL-SOI-CROSS 599 s for
601 game s**.

A finding, not just a cut: the spec sizes the SOI crossing as "~600 game-s at 10x = ~60
wall s of dense sampling" (`V1...toml:225-231`), but it runs at 1x. The machine issues ONE
`set_rails_warp 2` on the frame UT reaches the SOI target
(`mlib.py:10051-10056`), while the preceding native `warp_to` is still completing; the
stdout then logs `native warp_to completed at target ut=16137.435`, and 569 of the
crossing's telemetry rows read `warp=NONEx1.000`. `flight_efficiency` flags it
(`visit 10 DWELL-SOI-CROSS kind=slow-warp-ladder reached=False seconds=600.3
targetRate=10.0`). The crossing also ran 599.3 s against a 1,200-frame cap
(`soiDwellCrossFrames`, 0.5 s poll = 600 s), so it finished about one poll inside its own
flake bound.

What the gates read (`V1...toml:339`): the flight / commit / rewind tokens (all before the
dwell), `probe frame summary ... ghosts=[1-9]` (fires in DWELL-HOLD-1X already), and
`faithful-parity summary sampled=[1-9]`, which fired 12 times: three during the 1x hold
(12:12:38-12:12:48) and nine across ~40 s bracketing the recorded SOI crossing
(12:18:11-12:18:52). The mission row `dwellCrossedRecordedSoiUt` needs the crossing UT
inside the phase. `allowedAnomalies = []` (`:269`) sweeps the whole run.

Proposal: `soiDwellLeadSeconds` 300 -> 60 and `soiDwellTrailSeconds` 300 -> 60. At the 1x
the lane actually runs, that is ~120 wall s, which still brackets the ~40 s parity window
and lands at roughly the ~60-120 s the spec intended. Saves ~480 s on every nightly run and
takes the crossing far off its frame cap. SAFE\* for the same sweep-coverage reason as A4.
Separately (not a spec cut), the machine should re-assert the cross factor after the native
warp completes, or the comment should stop promising 10x; that is a mission fix for its own
PR and would change the anomaly sweep's warp regime, so it needs its own reading flight.

### A6. kx_rewind_watch PLAYBACK-WAIT margin (low value)

`PLAYBACK-WAIT` replays the committed recording at 1x until `recording_end_ut +
playbackMarginSeconds` (default 30 s, `mlib.py:25727`, read at `:25857`). Measured waits:
GS-4 134 s, GS-9 135 + 134 s, GS-12 134 s, GS-10 123 s, GS-11 122 s, RF-14 135 s, RF-15
134 s, MC-4 132 s, GS-6 230 s, GS-8 122 s, GS-7 349 s. The replay itself is the subject
(watch mode, ghost retirement, the GS-7 explosion hold), so only the margin is a candidate:
30 -> 15 saves ~15 s per wait. The margin exists "so the retire happens INSIDE the recorded
log"; the watch hold after a Destroyed terminal is 5 s (GS-7 header), so 15 s still covers
it, but `requireBalanced` lifecycle gates and the `MeshDestroyed ... reason=` tokens fire in
that window. SAFE\*, one confirming flight per distinct shape (GS-4, GS-7, GS-12).
Proposed only because it multiplies across 11 lanes; it is the lowest-priority row.

### A7. GS-2 family `postDeployDwellSeconds` (low value)

`postDeployDwellSeconds = 45` (`GS-2...toml:288`) is the 1x POST-DEPLOY hold in five lanes
(GS-2, GS-3, RF-18, RF-20 operator; ST-2 nightly), measured 45.0-45.7 s each. The spec says
it is the recording budget so both vessels carry real post-split trajectory and
`PruneSinglePointDebrisLeaves` cannot remove the leaf under test. None of the five pins a
point count (`[expectations.recordings.points]` absent on all five). 45 -> 20 is still
tens of samples at the 0.5 s poll. ~25 s per lane. SAFE\*, low value.

### A8 / A9. Dwells that are the thing tested (NO)

- **B13 / B14 `landedDwellSeconds = 120`** (`B13...toml:412`): the spec says the dwell "IS
  the recorded landed-on-another-body coverage the whole lane exists for", and it was
  closed from measurement. LANDED-SETTLE measured 120.5 / 120.8 s. NO.
- **GS-7 PLAYBACK-WAIT (349 s)**: the lane exists to watch the replay into the impact, the
  explosion and the 5 s watch hold. NO. Warping any kx replay would be a machine change
  that alters watch-mode behaviour; not proposed.
- **B-lane PARK dwells** (`parkDwellSeconds = 180`): already run at 4x physics warp
  (~45 wall s) and `flight_efficiency` shows 0-1 s each. Nothing to cut.

## 3. Shared flights (ruling B)

Method: `tomllib` over all 384 `harness/scenarios/*.toml`; grouped by (driver kind,
mission, the WHOLE `[fixture]` table). The full fixture table matters: grouping on
`saveTemplate` alone falsely paired RVR-7 / RVR-10..15 / RVR-20, which differ only in
`fixture.resources`. Within each group, a pair is a merge candidate when the absorbed lane's
step list, after dropping observability-only `SetSetting`s (verboseLogging,
ghostRenderTracing, mapRenderTracing), is a prefix of the host's and the mission params
differ only by an extension key. For each candidate the absorbed lane's expectations were
then EVALUATED against the host's latest PASS run: every `logContracts` required / forbidden
regex against the host `KSP.log.gz`, and the count / structure / rewind / ghostLifecycle /
unityExceptions windows against the host's verifier `observed` rows (scratch script, not
committed). The seam-only lanes (281 of 384) produced no prefix pair once the fixture table
was compared in full.

### Groups found (same mission + same full fixture)

| Group | Members (tier, p50 s) | Verdict |
|-------|-----------------------|---------|
| kx_rewind_watch / gs1-two-stage-pad | GS-4 (n, 338), GS-9 (n, 491), GS-6 (n, 517), GS-8 (n, 645), GS-7 (o, 767), GS-10 (o, 342), GS-11 (o, 329), GS-12 (o, 432), MC-4 (o, 326), RF-13H (o, 163), RF-14 (o, 339), RF-15 (o, 345), RF-9 (o, 210) | B1 only (GS-4 into GS-9). The rest differ in mission params that change the flight (impact profile, coast profile, part sweep craft, launch site, debris promotion, loop stages) or branch after the mission. |
| m3 / duna-direct-recorded | V2, V3F, V3R (all o, ~953) | NO: the playback mode (forced faithful vs engaged) and the scene (map vs flight) are exclusive per flight. Their cost is the dwell, handled in A2-A4. |
| gs1_auto_chute_booster / gs1-two-stage-pad | GS-1 (o), CA-1 (n), QL-4 / 4b / 4c (n), RF-1 / RF-4 (o), VN-1 / VN-2 / VN-9 (n) | NO: GS-1 and CA-1 differ in mission params (`siblingAirborneAtExit`, `focusImpactAtExit`); the QL lanes mutate a LIVE recorder differently after the same flight; RF-1 into RF-4 and VN-2 into VN-9 checked and conflict (below). |
| b2_lko_ascent / b2-lko-craft | B2, PWR-1, PWR-2, PWR-3 (all n, ~290) | B2 into PWR-2 (B2). PWR-1/2/3 set different `samplingDensity` before the flight, so they cannot share one. |
| cl1_pod_impact / career-pad-craft | CL-1, CL-2 (n, ~160) | B3. |
| cl3_refly_crew_tombstone / rewind-crew-loss | CL-3 (o), CL-4 (o), QL-3 (n) | CL-3 into CL-4 (B4). QL-3 reloads a pre-flight save at the end, so its final save differs. |
| gs2_orbital_probe_deploy / gs2-orbital-stack | GS-2, GS-3, RF-18, RF-20 (o), ST-2 (n) | NO: post-mission steps diverge (switch clicks, EVA, RunTests); RF-18 into RF-20 checked and conflicts. Missions are ~50 s; little to gain. |
| ci9_tip_dock / chain-tip-dock | CI-9 (n), VN-10 (n), VN-4 (n) | NO: CI-9 into VN-10 checked and conflicts; VN-4 uses a different target. |
| science_bench_recover / career-science-pad + rewind-readback | RB-1, RB-2 (o, 400) | NO: RB-1 hires a kerbal before the save to force the divergence RB-2 must not see. Mutually exclusive outcomes. |
| science_bench_recover / career-same-name-pad | L6-natural-dwell, L6-recover (o) | NO: `preRecoverDwellSeconds` 0 vs 12 is the variable under test. |
| b25_laythe_orbit / jool-park-nerv | B25, B33, B34, B35 (o) | NO: different target bodies. |
| forge_lko, forge_station / bdock-forge-base | FORGE-* (o) | NO: different craft / `parkAttached`; fixture producers. |
| eva4_atmo_chute / b1-pad-craft | EVA-4, M3 (n) | NO: `autoRecordOnLaunch` true vs false. |
| r1_rewind_loop / rewind-b9 | R1, RF-12L (o) | NO: `reflyConclusionProfile` differs. |
| rf12s_refly_orbit_insert / refly-split-crewed-recorded | RF-13, RF-19 (o) | NO: diverge right after the rewind (EVA vs none). |

The task brief's other examples were checked and are not strict prefixes: **B18 vs B19**
use different ascent machines and parks (B18 is `b2_lko_ascent` to an 80 km park on
`b18-dres-pad`; B19-B22 fly their own MechJeb ascent to a 700 km park), so B19 does not
contain B18's flight. **B11 vs B13 and B12 vs B14** commit at different points (orbiting
vs landed), so the orbit lane's terminal tokens never fire on the landing flight.

### B1. GS-4-kerbalx-rewind-watch into GS-9-kerbalx-repeat-rewind (nightly) - SAFE

IMPLEMENTED on branch `shared-flight-merges` (2026-10-14): GS-4's 4 missing required
tokens, its rewindPoints {1,1} pin, its unityExceptions block (maxTotal 6,
maxParsekFrames 0) and its D1 / D5 / D12 / D14 / D16 claims moved to GS-9; GS-4 deleted.
The merged GS-9 spec re-evaluated offline PASS against `2026-10-11_1542`. Confirm flight
owed.

Same mission, fixture and steps; the only param difference is `rewindCycles = 2` on GS-9
(GS-9's header: "REPEAT-REWIND IDEMPOTENCE on the GS-4 subject"). Evaluated against host
run `2026-10-11_1542_GS-9-kerbalx-repeat-rewind`: GS-4's 20 required tokens all present,
forbidden clean; recordings.count 8 in {8,8}; trees 1, committedTrees 1, recordings 8,
JointBreak 5 >= 4, SubOrbital 2 >= 2, Destroyed 6 >= 4; rewindPoints 1 in {1,1},
supersedeRows 0, tombstones 0; ghostLifecycle spawned 8 >= 8 (GS-9 also declares
`requireBalanced`); unityExceptions total 1 <= 6, parsekFrames 0; anomalies none. Saves
~325 s per nightly. Cost: a red in GS-9's second cycle now also reds GS-4's single-cycle
claims, and GS-4's `[dimensionsCovered]` move to GS-9.

### B2. B2-lko-ascent into PWR-2-physwarp-ascent-medium (nightly) - SAFE

IMPLEMENTED on branch `shared-flight-merges` (2026-10-14): B2's gates were already on
PWR-2 verbatim; its D1 / D3 / D4 claims moved; B2 deleted. The merged PWR-2 spec
re-evaluated offline PASS against `2026-10-12_0741`. Confirm flight owed.

PWR-2 flies "B2's LKO ascent" with `samplingDensity = 1`, which is the default
(`ParsekSettings.cs:213`), then adds a 4x physics-warp coast before `CommitTree`. Evaluated
against `2026-10-12_0741_PWR-2`: B2's 3 required tokens present, forbidden clean,
recordings.count 8 in {7,8}. Also holds on PWR-1 (`2026-10-12_0737`). Saves ~272 s per
nightly. Cost: B2's committed recording would no longer end at the circularization; it
carries the physics-warp tail. If B2 is meant as the un-warped control for the PWR series,
keep it; nothing in the PWR specs reads B2's results.

### B3. CL-1-pod-impact into CL-2-pod-impact-ledger (nightly) - SAFE, owner call

IMPLEMENTED on branch `shared-flight-merges` (2026-10-14), the owner accepting the
reversal: CL-1's gates and claims were already on CL-2; CL-1 deleted and its
no-commit / no-ledger atom cells retired. The merged CL-2 spec re-evaluated offline
PASS against `2026-10-11_1443`. Confirm flight owed.

Evaluated against `2026-10-11_1443_CL-2`: CL-1's 8 required tokens present, forbidden
clean, recordings.count 1 in {1,1}. Saves ~154 s per nightly. CL-2 adds `autoMerge = true`
before the flight and an `ExitToSpaceCenter` after it; CL-1 never commits, so the setting is
inert for CL-1's surface on this run. BUT the CL-2 header records a deliberate decision to
keep CL-1 as a separate atom ("a NEW spec id keeps CL-1 the atom ... and lets the coverage
report show the atom and its extension separately"), guarded by
`test_cl1_crew_loss.py::test_the_spec_drives_no_commit_and_declares_no_ledger_block`.
Accepting B3 reverses that choice.

### B4. CL-3-refly-crew-tombstone into CL-4-refly-crew-standin (operator) - SAFE

IMPLEMENTED on branch `shared-flight-merges` (2026-10-14): CL-3's gating
recordings.structure block and its D1 / D9 / D12 claims moved to CL-4 (its rewind
floors are implied by CL-4's tighter tombstones >= 2); CL-3 deleted. The merged CL-4
spec re-evaluated offline PASS against `2026-10-12_1813`. Confirm flight owed.

CL-4 is CL-3 plus `verboseLogging = true`. Evaluated against `2026-10-12_1813_CL-4`: 8
required present, forbidden clean, count 4 >= 1, trees 1, committedTrees 1, recordings 4,
supersedeRows 1 >= 1, tombstones 2 >= 1, unityExceptions 0. Saves ~64 s.

### B5. B17-duna-direct-orbit into V3C-flight-arrival-companion (nightly) - NEEDS-RE-PIN

Same mission (`b17_duna_direct`) and save; V3C adds the `looped-interplanetary` injected
corpus. Against `2026-10-12_0927_V3C`: B17's 6 required tokens present, forbidden clean,
rewind rows all zero; but recordings.count 4 vs B17's {3,3}, committedTrees 2 vs {1,1},
recordings 4 vs {3,3}. The moves are the injected corpus (+1 recording, +1 committed tree)
and are deterministic, so a re-pin is mechanical. Saves ~810 s per nightly, the largest
nightly item. The blocking caveat: B17 is the HARVEST SOURCE of `duna-direct-recorded`
(`harness/lib/test_saveparse.py:939-944`), the fixture V2 / V3F / V3R stand on. Harvesting
from a V3C run would carry the injected corpus into that fixture. Accepting B5 therefore
means keeping B17 as an operator-tier harvest lane, not deleting it.

### B6. B11-mun-orbit into V1-map-dwell-mun-orbit (nightly) - NEEDS-RE-PIN

V1's FLIGHT phase is the B11 profile ("byte-identical machine", `V1...toml:6-7`), with the
`rewind-b9` corpus injected. Against `2026-10-12_0859_V1`: B11's 7 required tokens present,
forbidden clean; recordings.count 12 vs B11's {9,9} (the injected corpus plus the discarded
re-fly residue; V1's own header calls the count "composition-shaped"). Saves ~715 s per
nightly. Same harvest caveat as B5: `mun-orbit-recorded` is a B11-profile harvest, and V1
rewinds and discards after the flight, so it cannot replace B11 as the harvest flight. An
exact re-pin is also weaker here because V1's residue depends on the discarded re-fly.

### B7. Checked and rejected (NO)

- **RF-18 into RF-20** (strict step prefix, same params): RF-18's 17 tokens hold on
  `2026-10-12_2108_RF-20`, but its save windows do not: recordings 7 vs {5,5},
  supersedeRows 4 vs max 0, tombstones 5 vs max 0. RF-18's "nothing superseded" claim cannot
  be read off a save RF-20 has since rewound and merged.
- **CI-9 into VN-10**: one required token missing (`listhandles kind=committed count=4`),
  count 6 vs {4,4}, spawnedVessels 2 vs {1,1}, vessel-name pins conflict (VN-10 renames).
- **RF-1 into RF-4**: one required token missing, forbidden `Reaped rp=` hit, rewindPoints 0
  vs {1,1} (RF-4's second rewind reaps the RP RF-1 asserts). Note: RF-1's latest run
  (`2026-10-12_2026`) is PARSEK-FAIL.
- **VN-2 into VN-9**: 10 of VN-2's 17 tokens pin the `VN2 ...` names VN-9 never types; a
  rename re-pin would make VN-9's flight carry both name sets, which is a different lane.

### An option not proposed: branch-point fixtures

Where lanes share a flight and then branch (RB-1 / RB-2 after `SaveGame rb-post-recovery`,
~340 s mission each; RF-14 / RF-15 after the kx mission, ~282 s each; RF-1 / RF-4 / VN-2 /
VN-9 after `ExitToSpaceCenter` + `SaveGame persistent`, ~97 s each), the shared prefix could
be flown once and harvested as a fixture the branches start from. That converts flown lanes
into fixture lanes: they stop exercising the live flight they currently re-fly, and the
fixture needs a re-harvest whenever the recording schema or the flight changes. It is listed
for completeness and needs its own owner ruling; it is not in the totals.

## 4. Merge mechanics and cost

There is no harness mechanism for one flight carrying several verdict sets (grep of
`hlib.py` / `run.py` for a shared-flight key found none). Accepting a B row today means
moving the absorbed spec's expectations, `[dimensionsCovered]` and logContracts into the
host spec and retiring the absorbed spec id, which touches the harness test cells that name
it (for example GS-4 in `test_hlib.py`; CL-1 in 11 harness files including
`test_cl1_crew_loss.py`; B2 in `test_doc_spec_sync.py`, `test_flighteff.py`,
`test_shells.py`) and `docs/dev/autotest-status.md`. The cost in every case: failure
attribution gets coarser (a red in the host's own extension reds the absorbed claims too),
and a lane-level flake now costs both claim sets one retry.

## 5. What I did not check

- The render manifest itself (not archived), so the A1 claim that closed `dwells` come from
  leg jumps rather than hold tails is inferred from counts (2 closed = 2 legs), not read.
- A1, A4, A5, A6, A7 were not flown; SAFE\* rows each owe one confirming flight.
- `requireBalanced` and `destroyedReasons` lifecycle clauses and any `[expectations.ledger]`
  block were not auto-evaluated in the merge checks (B1's host declares `requireBalanced`
  itself; B3's host is the ledger lane).
- `[dimensionsCovered]` transfer and the coverage registry consequences of retiring a spec.
- Merge checks used ONE host run each; a second host run would harden every SAFE in B.
- Seam-only lanes (281) were only checked for step-prefix pairs on identical fixtures; no
  attempt was made to batch unrelated read-only seam lanes onto one boot (each pays ~35-45 s
  of KSP boot), which is a different proposal.
- `duration.json` p50 is stale for lanes whose shape changed (B11 p50 1,317 s vs latest
  715 s); savings use the latest archived run where the two differ.
- The daily tier was scanned but not read lane by lane beyond the dwell and grouping passes.

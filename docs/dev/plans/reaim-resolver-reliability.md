# M-MIS-1: Re-aim resolver reliability - deterministic E2E + measurement harness

*Plan for the first Missions completion milestone (`docs/dev/todo-and-known-bugs.md`, M-MIS-1).
Branch `reaim-resolver-reliability`.*

## 1. Problem

`ReaimEndToEndInGameTest.Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer` (SPACECENTER
in-game test) is flaky, with the failure mode varying by the live UT at run time:

- **Mode A** (2026-06-06): all 5 windows resolved but `lan0=lanLast=0.00`, failing
  "the transfer orientation must rotate across windows".
- **Mode B** (2026-06-06, later run): `window k=2 must resolve a re-aimed transfer`.
- **Mode C** (2026-06-07): `window k=1 must resolve`.

Every later re-aim milestone (M-MIS-2/3/6/7) builds on
`ReaimPlaybackResolver.TryResolveWindowSegments`, so an irreproducible E2E blocks all of them.
The M-MIS-1 requirements (todo file): (1) make the E2E deterministic with pinned departure
UTs covering each observed failure mode; (2) measure before any knob math; (3) treat
"the faithful render is good enough for window k" as a valid outcome - the eventual fix may
be widening the tof search or classifying a window unresolvable-by-design, never stacking
solver heuristics.

## 2. Root-cause analysis (static, to be confirmed by the measurement run)

### 2.1 Mode A: the LAN assertion is degenerate for coplanar transfers (test-model defect)

`ReaimTransferSynthesizer.TrySynthesizeTransfer` projects the target endpoint onto the
LAUNCH body's orbital plane (the near-180-degree Lambert singularity fix). Kerbin's orbit in
stock KSP has inclination 0, i.e. the launch plane IS the reference plane, so the synthesized
transfer has inclination ~0. For a zero-inclination orbit the ascending node is undefined:
`longitudeOfAscendingNode` is degenerate, and KSP's element extraction
(`Orbit.UpdateFromStateVectors`) pins it at 0 (or floating-point noise decides it). The test
asserts orientation rotation on LAN ALONE, so it measures the degeneracy, not the rotation.
Whether a run passes depends on whether rounding noise in the swizzle round-trip produces a
tiny nonzero inclination that makes LAN accidentally meaningful - which varies with the
live-UT-seeded geometry. That is the observed `lan0=lanLast=0.00`.

The per-window orientation rotation is real; it lives in the LONGITUDE OF PERIAPSIS
(LAN + argumentOfPeriapsis, wrapped to [0,360)). CONFIRMED against KSP 1.12.5's element
extraction (decompiled `Orbit.UpdateFromFixedVectors`, the body of `UpdateFromStateVectors`):
when the node vector `an = cross(z, h)` is degenerate (equatorial orbit) KSP substitutes
`Vector3d.right`, making LAN exactly 0 and argumentOfPeriapsis the in-plane angle from +X to
the eccentricity vector with the correct handedness sign, i.e. AoP IS the longitude of
periapsis in that regime. In the noise-perturbed regime (tiny nonzero inclination) LAN and
AoP are measured from the same noisy node, so their SUM stays the well-defined longitude of
periapsis. LAN + AoP is therefore the robust orientation metric in both regimes, no fallback
needed. (Behavior described, not copied; decompiled source stays out of the repo.)

### 2.2 Modes B and C: band-edge departure selection + eccentric-target drift

The test seeds its scan at the live `Planetarium.GetUniversalTime()` (line 48) and takes the
FIRST departure (of 48 steps across one synodic period) whose Hohmann-tof transfer
synthesizes. Two consequences:

1. **Irreproducible by construction**: the scan origin moves with the live clock, so the
   chosen `goodDep` differs every run.
2. **Band-edge by construction**: the first success in a scan is the leading EDGE of the
   feasibility band (the contiguous run of departures that synthesize). The congruent-window
   model then relaunches at `goodDep + k*synodic`. Synodic recurrence is exact only for
   circular orbits; Duna's eccentricity (0.051) means that after k synodic periods the phase
   angle recurs but Duna's radius / true anomaly do NOT exactly recur, so window k's required
   transfer drifts. The resolver searches tof only +-6% around the recorded tof
   (`ReaimPlaybackResolver.BuildWindowSegments`, TofSearchStepFraction 0.005 x 12 steps).
   A mid-band departure has margin and converges (step 0 in almost every window, per the
   code comment); a band-edge departure has none, so some window k falls off the band and
   declines to faithful - which the strict assertion `window k must resolve` reports as
   failure. Whether the live UT lands the scan on a band edge or mid-band decides the run.

The resolver declining to faithful for an infeasible window is DESIGNED behavior (fail
closed, never garbage). The defect is (a) the test manufactures a fragile departure and then
asserts the strong claim on it, and (b) we have no measurement of the band shape / per-window
failure reasons to decide whether the +-6% search is too narrow (requirement 2: measure
first).

## 3. Scope

### This PR delivers

1. **Determinism**: every code path of the E2E seeds from a pinned constant UT, never the
   live clock. Same save, any save, any time: identical geometry, identical outcome.
2. **Failure-mode coverage as deterministic tests**: mode A via the longitude-of-periapsis
   assertion fix (any departure exercises it, all transfers are near-coplanar); modes B/C
   via an explicit pinned band-edge case asserting the WEAK (designed) contract.
3. **The measurement harness**: an in-game diagnostic sweep that maps the full feasibility
   band and per-window failure reasons, to be run once in-game; its KSP.log output is the
   data that decides any follow-up knob change.
4. **Pure-helper extraction + xUnit coverage** for everything extractable.

### Explicitly deferred (until the sweep has produced data)

- Any solver / search change: tof-search widening, geometry-aware tof centering (that is
  M-MIS-3 requirement 2), window classification surfaced to the UI. "Measure before knob
  math" is the milestone's own rule; this PR builds the instrument.
- A pure xUnit replica of the synthesis sweep (Kepler ephemerides + UvLambert off-Unity).
  It would prove a model of the path, not the path (`Orbit.UpdateFromStateVectors` +
  `PatchedConics.CalculatePatch` are the live half). The in-game sweep is the authoritative
  measurement; revisit a pure replica only if M-MIS-3 needs CI-side geometry tests.

## 4. Design

### 4.1 Pure helpers (new, unit-tested)

**`Reaim/ReaimFeasibilityScan.cs`** (internal static, pure):

- `int FirstSuccessIndex(IReadOnlyList<bool> scan)`: index of the first true, -1 when none.
- `int CenterOfLongestRunIndex(IReadOnlyList<bool> scan, bool cyclic)`: center index of the
  longest contiguous run of true entries; the scan covers exactly one synodic period, so the
  band can WRAP across the end (cyclic=true). Longest-run tie: first run in scan order.
  Even-length run: lower-middle index. Returns -1 when no entry is true.

These give the E2E its two deterministic departures (band-edge and mid-band) from one scan.

**`TransferWindowMath.LongitudeOfPeriapsisDegrees(double lanDegrees, double aopDegrees)`**:
`ClampDegrees360(lan + aop)`, NaN-propagating. One-liner, but it carries the degeneracy
rationale in its doc comment and gets theory tests (including the wrap and NaN cases).

### 4.2 Deterministic E2E rewrite (`InGameTests/ReaimEndToEndInGameTest.cs`)

Shared setup, extracted into a private helper used by all three tests:

- `PinnedScanBaseUT = 5_000_000.0` (a fixed constant, roughly Kerbin year 24; the value is
  arbitrary and NOT derived from any observed failure - any fixed value works because stock
  ephemerides are pure functions of UT and nothing in the driven path reads the live clock:
  the resolver's `currentUT` is synthesized from the schedule fields. Pinning the SCAN BASE
  pins everything downstream deterministically).
- Scan `PinnedScanBaseUT + (synodic * i) / 48` for i in 0..47, recording
  `TrySynthesizeTransfer` success per step (Hohmann tof, prograde) into a bool list.
- Skip cleanly (existing pattern) when Kerbin/Duna are absent or share no parent.

**Test 1 (strict, mid-band)**: `Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer`.
Departure = scan center via `CenterOfLongestRunIndex`. Same structure as today (plan +
schedule + 5 windows through `ReaimPlaybackResolver.Shared`), with two assertion changes:

- Orientation rotation asserted on `LongitudeOfPeriapsisDegrees(transfer.longitudeOfAscendingNode,
  transfer.argumentOfPeriapsis)` instead of LAN alone, comparing window 0 to window 4 with
  the existing > 1.0 deg threshold (per-window target motion is ~tens of degrees; the
  threshold needs no tuning). Compare on the wrapped difference (ClampDegrees180 of the
  delta) so a wrap across 360 cannot mask or fake a rotation.
- The summary log line gains `inc/lan/aop/lpe` per window-0 and window-last so a future
  failure is classifiable from the log alone.

**Test 2 (band-edge, weak contract)**:
`Reaim_KerbinToDuna_BandEdgeWindows_ResolveOrDeclineCleanly`.
Departure = `FirstSuccessIndex` (what the old test accidentally picked when the live UT was
unlucky; now pinned). Drive the same 5 windows; per window assert the DESIGNED contract:

- `TryResolveWindowSegments` either returns true with 3 sane segments (same checks as the
  strict test), or returns false with `segments == null` while `windowIndex == k` (clean
  decline to faithful, correctly indexed).
- Determinism within the run: `Clear()` the shared resolver cache and re-resolve the same
  window; assert the outcome (resolved flag + transfer elements when resolved) is identical
  to the first pass. This also locks the cache-vs-fresh equivalence.
- Log a per-window resolve/decline map (one Info line, batch-counted) so the user's run
  records which windows decline at the band edge.

This is the pinned regression case for modes B/C: if the resolver's decline behavior ever
turns into garbage segments or a wrong index, this fails deterministically. Test 2
deliberately makes NO claim that any window resolves at the band edge (all-decline is a
valid outcome); it documents failure modes B/C as designed fail-closed behavior and pins
their determinism. The strong claim belongs to test 1 only.

**Test 3 (measurement sweep, manual-only)**:
`Reaim_KerbinToDuna_FeasibilitySweep_Diagnostic`, `AllowBatchExecution = false` (the batch
runner skips it; run explicitly from the test runner UI, like the TS Fly canary). For each
of the 48 scan departures that synthesize at window 0, drive windows k = 0..4 through a
PRIVATE `ReaimPlaybackResolver` instance (not `.Shared`, so the sweep cannot pollute the
real cache) and log per (departure index, window): resolved or declined. The resolver's
existing per-window Verbose lines already carry the failReason (synth failure across the
tof search, body lookup, empty replacement); the sweep adds one summary Info line per
departure (batch-counting convention) plus a final band-shape summary: band run lengths,
first/center/last feasible index, resolve counts per window index. This is the "measure"
artifact: one in-game run produces the full feasibility map in KSP.log. Log shape (example):

```
[Parsek][INFO][ReaimE2E] sweep dep=17/48 depUT=5061234.5 window0=ok windows=okokxok resolved=4/5
[Parsek][INFO][ReaimE2E] sweep summary: feasible=9/48 band=[15..23] first=15 center=19
  perWindowResolved k0=9 k1=8 k2=7 k3=8 k4=9
```

### 4.3 Logging

- New sweep logs as above (`ReaimE2E` subsystem tag, Info for one-per-departure and final
  summaries; the per-window detail stays on the resolver's existing Verbose lines).
- Test 1 / test 2 summary lines extended with the element tuple and the resolve/decline map.
- No production logging changes: the resolver's existing decline logs are already one-shot
  per (member, window) thanks to the cache.

### 4.4 Production code changes

None to resolver / synthesizer / planner behavior. The only production-assembly edits are
the two new pure helpers (4.1), the test file rewrite (in `Source/Parsek/InGameTests/`,
which compiles into the mod assembly), and the summary-log additions inside those tests.
This keeps the PR reviewable as "harness + measurement", honoring the no-knob-math rule.

## 5. Tests

xUnit (`Source/Parsek.Tests/`):

- `ReaimFeasibilityScanTests`: theories for first-success and center-of-longest-run
  (empty, none-true, all-true, single run, two runs with tie, wrapped run across the
  boundary, even-length run, cyclic=false boundary behavior).
- `TransferWindowMathTests`: new theories for `LongitudeOfPeriapsisDegrees` (sum, wrap,
  NaN propagation).

In-game (the deliverable itself): tests 1-3 above. Expected first-run outcomes on stock:

- Test 1 passes (mid-band departure). If a window declines mid-band in-game, that is a REAL
  resolver bug with a deterministic repro: STOP and investigate (with the sweep map) before
  any further milestone work. Do NOT loosen test 1's assertion to make it pass; the strict
  claim on a mid-band departure is the resolver's actual contract.
- Test 2 passes by construction unless decline behavior is broken.
- Test 3 produces the feasibility map for the follow-up classification decision.

## 6. Verification recipe (user playtest, after merge or on the branch)

1. Build, verify deployed DLL (UTF-16 grep for `FeasibilitySweep`).
2. Space Center scene, Ctrl+Shift+T, run the Periodicity category (tests 1-2 run in batch),
   then run the sweep test manually.
3. `python scripts/collect-logs.py reaim-mmis1-sweep` and read the band map + per-window
   declines from KSP.log.
4. Re-run once more (any UT): both runs must pass, with the same band-center index and the
   same per-window outcomes on the strict test. (Exact summary-line equality is NOT expected;
   see section 9 - KSP's per-frame orbit re-basing adds knife-edge jitter.)

## 7. Documentation updates (same commit)

- `CHANGELOG.md`: one line under the current version (deterministic re-aim E2E + sweep
  diagnostic; flaky-test fix).
- `docs/dev/todo-and-known-bugs.md`: update the M-MIS-1 milestone entry (harness step
  done, what remains: run sweep, classify, then decide widen-vs-classify) and the FLAKY
  entry (root causes recorded, deterministic repro shipped, next step the measurement run).
- This plan file.

## 8. Risks

- **LPe degeneracy**: RESOLVED pre-implementation. The KSP 1.12.5 decompile (section 2.1)
  confirms LAN is pinned to exactly 0 for an equatorial orbit while AoP carries the in-plane
  periapsis longitude, so LAN + AoP is robust in both regimes. No fallback path needed.
- **Test 1 could fail in-game on a mid-band window decline**: that would be a genuine
  resolver bug surfaced with a deterministic repro (see section 5); the response is
  investigate-and-fix in a follow-up driven by the sweep data, never loosening the
  assertion. The PR's claim is "the outcome is deterministic and measured", not "the
  resolver never declines".
- **Sweep runtime**: worst case 48 departures x 5 windows x 25 tof attempts x
  (Lambert + CalculatePatch). Lambert and CalculatePatch are millisecond-scale; tens of
  seconds worst case as a manual-only test is acceptable. The sweep logs progress per
  departure so a long run is observable.

## 9. Measurement results (2026-06-10 in-game run, logs `2026-06-10_2213_reaim-mmis1-sweep`)

All four Periodicity tests PASSED across two batch runs plus the manual sweep, on the
0.10.0-line DLL built from this branch (signature strings verified in the deployed DLL and
the log).

**Mode A fix validated directly.** For the SAME window-0 transfer, one run reported
`lan=0.00 aop=36.99` and another `lan=259.52 aop=137.46` (inc=0.0000 both): KSP's LAN/AoP
split flipped completely on noise, while the longitude of periapsis was 36.99 in both. The
old LAN-only assertion would have flaked again; the LPe assertion held, and the orientation
rotated ~36.99 -> ~209 degrees across the 5 windows as the model predicts.

**Resolver robustness measured.** 17-18 of 48 scan departures feasible; band map
`.XXXXXXX.X.XXX.XXXXX.....X......` (a solid contiguous band at indices 1-7 plus scattered
marginal singles). EVERY departure in the contiguous band resolved ALL 5 windows
(per-window resolve counts 17,16,17,17,17). Observed declines were exclusively at isolated
knife-edge departures: dep=25 window 1 (Lambert non-convergence across the +-6% search,
both sweeps) and, in the second sweep only, dep=43/44 (the ~180-degree retrograde-branch
direction mismatch). All declines were clean fail-closed faithful fallbacks.

**Determinism is per-frame, not absolute (claim corrected).** The feasible count flickered
17..20 and the band-EDGE index flipped 1<->2 across invocations seconds apart, while the
contiguous band and its CENTER (index 4) were stable in every invocation, and the band-edge
test's same-frame cache-cleared re-solve matched exactly every time. Cause: KSP re-bases
each body orbit's epoch/meanAnomalyAtEpoch every frame, so positions at a FIXED UT carry
~1e-15 relative frame-dependent rounding noise, which only flips knife-edge entries. The
tests are insensitive by design (band-center selection; edge test asserts the contract, not
the edge identity); both batch runs passed.

**M-MIS-1 classification decision: NO tof-search widening.** The +-6% recorded-tof search
resolves every window across the entire contiguous feasibility band; declines occur only at
departures that are themselves marginal (their very feasibility flickers with frame noise),
and a real recorded mission's departure lies inside the band by definition (the player flew
that transfer). Knife-edge window declines are hereby classified UNRESOLVABLE-BY-DESIGN:
the fail-closed faithful fallback is the correct, designed behavior. Geometry-aware tof
centering remains M-MIS-3 scope (eccentric/inclined targets), unchanged by this decision.

## 10. Observed-failure pin (2026-06-11 run)

A 2026-06-11 18:06 SPACECENTER batch (logs `2026-06-11_1811_m1-ingame-tests`, KSP.log
~:9952) failed `Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer` with "window k=2 must
resolve a re-aimed transfer (congruent-window model)". DLL provenance check (per the
"diagnosing which build produced a collected log" recipe): the session loaded `Parsek'
V0.10.0` built from the logistics-m1-origin-debit worktree at a3f0aaeb9, which does NOT
contain the PR #1116 merge (fddf134ba) - so the run executed the OLD live-UT-seeded strict
test this plan replaced, and the failure is the documented pre-fix flake, not a regression
in the shipped harness.

What makes the run valuable: it is the first observed failure whose geometry is fully
reconstructable from the log + collected saves. Reconstruction:

- live UT at seed ~5.5s (fresh `temp_sandbox` save; corroborated by the C2 canary scan in
  the same second: firstEncounterDepUT=2728574.5 = 5*synodic/36 + ~5.5)
- first-scan-success departure = `409290.81937705079` (= liveUT + 1 * synodic/48, the
  leading band EDGE by construction; member id `reaim-e2e-409291`)
- window 0 resolved exactly (recorded tof 6524002.7336873859; arrival 13.2Mm inside Duna's
  47.9Mm SOI - not knife-edge)
- window 1 resolved at departUT=20054988.069 with tof stretched to 6850202.87 (+5.0%,
  inside the +-6% search)
- window 2 (departUT=39700685.319573924) DECLINED across the whole +-391440s (+-6%) tof
  search: "transfer direction mismatch inc=180.00 deg (resultRetrograde=True,
  recordedRetrograde=False)" - faithful this window.

**Classification (consistent with section 9, no new mode):** the retrograde-branch
direction mismatch. Duna's eccentric drift from the band-edge departure pushes window 2's
transfer angle past 180 degrees, so every plane-constrained Lambert candidate in the tof
band travels the wrong way (coplanar inc=180) and `ReaimTransferSynthesizer` rejects it;
the resolver falls back to faithful with the per-window decline reason already logged.
Window 1 carried half the drift and recovered at +5.0% tof; window 2 needs roughly double,
outside the band. This is the same knife-edge mode as sweep dep=43/44, already classified
UNRESOLVABLE-BY-DESIGN (section 9); the clean fail-closed decline is the designed outcome.
No tof widening, per the section 9 decision (geometry-aware centering stays M-MIS-3).

**Shipped (branch `reaim-pin-observed-edge`):** the departure is pinned as
`ObservedEdgeDepartureUT = 409290.81937705079` and driven by the new
`Reaim_KerbinToDuna_ObservedEdgeDeparture_ResolvesOrDeclinesCleanly` in-game test under the
weak band-edge contract (resolve-or-decline-cleanly + deterministic re-solve), plus a
window-0-must-resolve premise check; the per-window R/d map is logged on every run. This
completes the M-MIS-1 item 1 clause "including at least one UT from each observed failure
mode" with a real observed UT (the 2026-06-10 harness pinned the band-edge MECHANISM, but
the earlier failures' UTs were never recoverable). The band-edge drive loop is shared
(`DriveWindowsResolveOrDeclineCleanly`), and the geometry resolution was split out of the
scan (`BuildGeometryOrSkip`) so the pinned test skips the 48-step scan cost.
